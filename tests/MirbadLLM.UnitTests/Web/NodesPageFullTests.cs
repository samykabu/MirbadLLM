using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Api;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Ui;
using MirbadLLM.Web.Enrollment;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707, CA2000, xUnit1051 // underscore test names; the context owns the services it is given; the real services take no test token.

/// <summary>
/// MN-117: the complete Nodes page and node detail (design §12.1), rendered with bUnit over the real
/// <see cref="NodeAdminService"/> (MN-102), <see cref="JsonNodeRegistry"/>, <see cref="JsonModelRegistry"/>,
/// <see cref="JsonlAuditLog"/> (MN-116), <see cref="AdminReauthenticator"/> and <see cref="EnrollmentService"/>; only the
/// live status cache, the metrics history and the fencing coordinator are hand-written fakes. Covers the Add node
/// dialog (token, per-OS commands with a prompt variant, copy, live wait, approve with the pin, reject), the row "⋯"
/// menu actions (approve, reject, drain / undrain, rekey, revoke with typed confirmation and admin re-auth), the detail
/// tabs (GPUs, Models, Operations, Recent commands, Certificates with CA rotation, Settings) and the single-DGX view.
/// </summary>
public sealed class NodesPageFullTests : IAsyncDisposable
{
    private const string Password = "correct horse battery staple";
    private const string AgentIp = "192.0.2.50";

    // Generous: the real registries do file I/O and the suite shares the machine with other suites.
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static readonly NodeApiCaller TestCaller = new("admin:test", "test-client") { User = TestAdmin.User };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-nodes-ui-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    // One CA for the class: creating one per test costs more than the rest of the test, and nothing here changes it.
    private static readonly CertificateAuthority SharedCa = CertificateAuthority.Create();
    private readonly CertificateAuthority _ca = SharedCa;
    private readonly FakeCache _cache = new();
    private readonly FakeMetricsHistory _history = new();
    private readonly BunitContext _ctx = new();
    private BunitJSModuleInterop? _module;
    private BunitAuthorizationContext? _auth;
    private JsonNodeRegistry? _nodes;
    private JsonModelRegistry? _models;
    private JsonlAuditLog? _audit;
    private GatedNodeRegistry? _gatedNodes;

    private string DataDir => Path.Combine(_root, "data");

    private JsonNodeRegistry Nodes => _nodes ??= new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = DataDir }), _time);

    private JsonModelRegistry Models => _models ??= new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = DataDir }));

    private JsonlAuditLog Audit => _audit ??= new JsonlAuditLog(Path.Combine(DataDir, "audit"), _time);

    private NodeAdminService Admin => _ctx.Services.GetRequiredService<NodeAdminService>();

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        _nodes?.Dispose();
        _models?.Dispose();
        _audit?.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    // ---------- Add node ----------

    [Fact]
    public async Task AddNode_CreatesTheToken_ShowsPromptAndOneLineCommands_WaitsLive_AndApprovesThePinnedKey()
    {
        Setup();
        var cut = RenderList();

        OpenAddNode(cut);
        Assert.Equal("true", cut.Find("[data-testid=add-node]").GetAttribute("data-open"));
        Assert.True(cut.Find("[data-testid=add-node-confirm]").HasAttribute("disabled"));

        // The id is checked as it is typed.
        InputAt(cut, "[data-testid=add-node-id]", "PC Office");
        Assert.Contains("lower-case letters", Text(cut, "add-node-id-error"), StringComparison.Ordinal);
        InputAt(cut, "[data-testid=add-node-id]", "pc-office");
        Assert.Empty(cut.FindAll("[data-testid=add-node-id-error]"));
        InputAt(cut, "[data-testid=add-node-name]", "Office PC");
        cut.ChangeAt("[data-testid=add-node-ttl]", "30");
        cut.ClickAt("[data-testid=add-node-confirm]");

        // The command: a token prompt by default, the token beside it; the one-line form carries it in the environment.
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=add-node-command]")), Wait);
        string token = Text(cut, "add-node-command-token");
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        string prompt = Text(cut, "add-node-command-linux-prompt");
        Assert.StartsWith("sudo ./install-agent.sh --node-id pc-office --enroll-url https://", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(token, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("MIRBADLLM_JOIN_TOKEN", prompt, StringComparison.Ordinal);
        Assert.Contains("--ca-hash sha256:", prompt, StringComparison.Ordinal);
        Assert.Contains("(in 30 min)", Text(cut, "add-node-command-expires"), StringComparison.Ordinal);
        Assert.StartsWith("sha256:", Text(cut, "add-node-command-ca-hash"), StringComparison.Ordinal);
        Assert.Contains("Windows and macOS", Text(cut, "add-node-command-os-note"), StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=add-node-waiting]"));

        cut.ChangeAt("[data-testid=add-node-command-mode-inline]", true);
        string inline = Text(cut, "add-node-command-linux");
        Assert.Equal($"sudo MIRBADLLM_JOIN_TOKEN='{token}' {prompt["sudo ".Length..]}", inline);

        // Copy sends the exact text.
        cut.ClickAt("[data-testid=add-node-command-linux-copy]");
        Assert.Equal(inline, _module!.Invocations["copyText"][^1].Arguments[0]);

        // The token is never in the URL.
        Assert.DoesNotContain(token, _ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);

        // The agent enrolls with it; the dialog moves on to approval and shows what to check.
        _time.Advance(TimeSpan.FromSeconds(20));
        string pin = await EnrollAsync(token, "pc-office");
        cut.ClickAt("[data-testid=add-node-check]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=add-node-enrolled]")), Wait);
        Assert.Equal(Spki.ToDisplayFingerprint(pin), Text(cut, "add-node-facts-fingerprint"));
        Assert.Equal(AgentIp, Text(cut, "add-node-facts-from"));
        Assert.Contains("Ubuntu 24.04 LTS · amd64", Text(cut, "add-node-facts-os"), StringComparison.Ordinal);
        Assert.Equal("1 × NVIDIA GeForce RTX 4060 Ti 16.0 GiB", Text(cut, "add-node-facts-gpus"));
        Assert.Equal("Approve", cut.Find("[data-testid=add-node-confirm]").TextContent.Trim());

        cut.ClickAt("[data-testid=add-node-confirm]");

        cut.WaitForAssertion(() => Assert.Contains("is approved", Text(cut, "nodes-status"), StringComparison.Ordinal), Wait);
        Assert.Equal("false", cut.Find("[data-testid=add-node]").GetAttribute("data-open"));
        NodeRecord record = (await Nodes.GetAsync("pc-office"))!;
        Assert.True(record.Approved);
        Assert.True(Spki.PinEquals(pin, record.CertSpkiSha256!));
        Assert.Equal("Office PC", record.DisplayName);
        Assert.NotNull(cut.Find("[data-testid=nodes-row-pc-office]"));

        // Audit lines for both steps, none holding the token.
        var lines = await Audit.ReadAsync(new AuditQuery { NodeId = "pc-office" });
        Assert.Contains(lines, l => l.Action == AuditActions.NodeJoinToken && l.Result == AuditResults.Succeeded);
        Assert.Contains(lines, l => l.Action == AuditActions.NodeApprove && l.Result == AuditResults.Succeeded);
        string auditText = string.Concat(Directory.GetFiles(Path.Combine(DataDir, "audit")).Select(File.ReadAllText));
        Assert.DoesNotContain(token, auditText, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddNode_AnonymousSession_IsRefusedInPlace()
    {
        Setup();
        _auth!.SetNotAuthorized();
        var cut = RenderList();
        OpenAddNode(cut);
        InputAt(cut, "[data-testid=add-node-id]", "pc-office");

        cut.ClickAt("[data-testid=add-node-confirm]");

        Assert.Contains("Sign in to the portal", Text(cut, "add-node-error"), StringComparison.Ordinal);
        Assert.Equal("true", cut.Find("[data-testid=add-node]").GetAttribute("data-open"));
        Assert.Empty(cut.FindAll("[data-testid=add-node-password]"));
        Assert.Empty(await Nodes.ListJoinTokensAsync());
        Assert.Contains(await Audit.ReadAsync(new AuditQuery { NodeId = "pc-office" }),
            l => l.Action == AuditActions.NodeJoinToken && l.Result == AuditResults.Refused && l.ErrorCode == ErrorCodes.ReauthenticationFailed);
    }

    [Fact]
    public async Task AddNode_IdCheck_RefusesEnrolledRevokedAndReservedIds()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("dgx"));
        await Nodes.UpsertAsync(Approved("old"));
        await Nodes.RevokeAsync("old");
        var existing = await Nodes.ListAsync();

        Assert.Contains("already enrolled", AddNodeDialog.IdRefusal("dgx", existing), StringComparison.Ordinal);
        Assert.Contains("revoked", AddNodeDialog.IdRefusal("old", existing), StringComparison.Ordinal);
        Assert.Contains("reserved", AddNodeDialog.IdRefusal("join-tokens", existing), StringComparison.Ordinal);
        Assert.NotNull(AddNodeDialog.IdRefusal("-bad", existing));
        Assert.NotNull(AddNodeDialog.IdRefusal(new string('a', 41), existing));
        Assert.Null(AddNodeDialog.IdRefusal("pc-office", existing));

        var cut = RenderList();
        OpenAddNode(cut);
        InputAt(cut, "[data-testid=add-node-id]", "dgx");
        // The dialog reads the registry when it opens; the check applies as soon as that read is in.
        cut.WaitForAssertion(() => Assert.Contains("Re-pair it", Text(cut, "add-node-id-error"), StringComparison.Ordinal), Wait);
        Assert.True(cut.Find("[data-testid=add-node-confirm]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task AddNode_IdCheck_WaitsForTheRegistryRead_SoATakenIdNeverLooksFree()
    {
        // W8 review (MN-128): the check ran against an empty list until the dialog's read finished.
        _gatedNodes = new GatedNodeRegistry(Nodes);
        Setup();
        await Nodes.UpsertAsync(Approved("dgx"));
        var cut = RenderList();

        _gatedNodes.Hold();
        cut.ClickAt("[data-testid=nodes-add]");
        InputAt(cut, "[data-testid=add-node-id]", "dgx");

        Assert.Equal(["1Details", "2Joincommand", "3Approve"], cut.FindAll("[data-testid=add-node-steps] li").Select(li => li.TextContent.Replace(" ", "", StringComparison.Ordinal).Trim()).ToArray());
        Assert.Equal("step", cut.Find("[data-testid=add-node-steps] li").GetAttribute("aria-current"));
        Assert.NotNull(cut.Find("[data-testid=add-node-id-checking]"));
        Assert.Empty(cut.FindAll("[data-testid=add-node-id-error]"));
        Assert.True(cut.Find("[data-testid=add-node-confirm]").HasAttribute("disabled"));

        _gatedNodes.Release();
        cut.WaitForAssertion(() => Assert.Contains("Re-pair it", Text(cut, "add-node-id-error"), StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("[data-testid=add-node-id-checking]"));
        Assert.True(cut.Find("[data-testid=add-node-confirm]").HasAttribute("disabled"));
        Assert.Empty(await Nodes.ListJoinTokensAsync());
    }

    [Fact]
    public void AddNode_WithoutAdminLogin_ExplainsWhy_AndCannotCreateAToken()
    {
        Setup(loginEnabled: false);
        var cut = RenderList();
        OpenAddNode(cut);
        InputAt(cut, "[data-testid=add-node-id]", "pc-office");

        Assert.NotNull(cut.Find("[data-testid=add-node-login-disabled]"));
        Assert.Empty(cut.FindAll("[data-testid=add-node-password]"));
        Assert.True(cut.Find("[data-testid=add-node-confirm]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task AddNode_WithdrawToken_ExpiresItOnTheMaster()
    {
        Setup();
        var cut = RenderList();
        OpenAddNode(cut);
        InputAt(cut, "[data-testid=add-node-id]", "pc-office");
        cut.ClickAt("[data-testid=add-node-confirm]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=add-node-command-token]")), Wait);
        string token = Text(cut, "add-node-command-token");

        cut.ClickAt("[data-testid=add-node-withdraw]");

        cut.WaitForAssertion(() => Assert.Contains("withdrawn", Text(cut, "nodes-status"), StringComparison.Ordinal), Wait);
        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await Nodes.TryConsumeJoinTokenAsync(token, "pc-office")).Outcome);
    }

    // ---------- Row menu: approve, reject, drain, revoke, rekey ----------

    [Fact]
    public async Task AgentUpdates_TheAutomaticSwitchStaysVisible_WhenEveryAgentIsCurrent_OnAndOff()
    {
        // No node runs another build: the switch is still there, so automatic updates can be turned on (and off) any time.
        Setup();
        await PendingAsync("pc-office");
        var cut = RenderList();

        Assert.Contains("Every agent runs MirbadLLM's build", Text(cut, "agent-updates"), StringComparison.Ordinal);
        Assert.Contains("Automatic updates are off", Text(cut, "agent-updates"), StringComparison.Ordinal);
        Assert.Equal("Update automatically after each MirbadLLM update", Text(cut, "agent-updates-automatic"));

        Assert.Empty(cut.FindAll("[data-testid=agent-updates-password]"));
        Assert.False(cut.Find("[data-testid=agent-updates-automatic]").HasAttribute("disabled"));
        cut.ClickAt("[data-testid=agent-updates-automatic]");
        cut.WaitForAssertion(() => Assert.Equal("Automatic agent updates are on.", Text(cut, "agent-updates-message")), Wait);
        Assert.True(Admin.UpdateSettings!.Automatic);
        Assert.Equal("Turn automatic updates off", Text(cut, "agent-updates-automatic"));

        cut.ClickAt("[data-testid=agent-updates-automatic]");
        cut.WaitForAssertion(() => Assert.Equal("Automatic agent updates are off.", Text(cut, "agent-updates-message")), Wait);
        Assert.False(Admin.UpdateSettings!.Automatic);
        Assert.Equal("Update automatically after each MirbadLLM update", Text(cut, "agent-updates-automatic"));
    }

    [Fact]
    public async Task RowMenu_PendingNode_OffersApproveAndReject_ApproveShowsAndPinsTheFingerprint()
    {
        Setup();
        string pin = await PendingAsync("pc-office");
        var cut = RenderList();

        Assert.Equal("pending-approval", cut.Find("[data-testid=nodes-state-pc-office]").GetAttribute("data-state"));
        var menu = cut.Find("[data-testid=nodes-more-pc-office-menu]");
        Assert.NotNull(menu.QuerySelector("[data-testid=nodes-approve-pc-office]"));
        Assert.NotNull(menu.QuerySelector("[data-testid=nodes-reject-pc-office]"));
        Assert.Null(menu.QuerySelector("[data-testid=nodes-revoke-pc-office]"));
        Assert.Null(menu.QuerySelector("[data-testid=nodes-drain-pc-office]"));

        cut.ClickAt("[data-testid=nodes-approve-pc-office]");
        Assert.Equal("true", cut.Find("[data-testid=node-approve]").GetAttribute("data-open"));
        Assert.Equal(Spki.ToDisplayFingerprint(pin), Text(cut, "node-approve-facts-fingerprint"));

        cut.ClickAt("[data-testid=node-approve-confirm]");

        cut.WaitForAssertion(() => Assert.Contains("is approved", Text(cut, "nodes-status"), StringComparison.Ordinal), Wait);
        NodeRecord record = (await Nodes.GetAsync("pc-office"))!;
        Assert.True(record.Approved);
        Assert.True(Spki.PinEquals(pin, record.CertSpkiSha256!));
        cut.WaitForAssertion(() => Assert.Equal("offline", cut.Find("[data-testid=nodes-state-pc-office]").GetAttribute("data-state")), Wait);
    }

    [Fact]
    public async Task Approve_IsRefusedInPlace_WhenTheNodeEnrolledAgainWithAnotherKey()
    {
        Setup();
        await PendingAsync("pc-office");
        var cut = RenderList();
        cut.ClickAt("[data-testid=nodes-approve-pc-office]");

        // Another enrollment (a new token, a new key) lands while the dialog shows the first fingerprint.
        var second = await Admin.CreateJoinTokenAsync(new JoinTokenCreateRequest { NodeId = "pc-office", Password = Password }, TestCaller);
        string newPin = await EnrollAsync(second.Token, "pc-office");

        cut.ClickAt("[data-testid=node-approve-confirm]");

        Assert.Contains("enrolled again with another key", Text(cut, "node-action-error"), StringComparison.Ordinal);
        Assert.Equal("true", cut.Find("[data-testid=node-approve]").GetAttribute("data-open"));
        NodeRecord record = (await Nodes.GetAsync("pc-office"))!;
        Assert.False(record.Approved);
        Assert.True(Spki.PinEquals(newPin, record.PendingCertSpkiSha256!));
    }

    [Fact]
    public async Task RowMenu_Reject_DeletesThePendingNode()
    {
        Setup();
        await PendingAsync("pc-office");
        var cut = RenderList();

        cut.ClickAt("[data-testid=nodes-reject-pc-office]");
        Assert.Contains("pending key", Text(cut, "node-reject"), StringComparison.Ordinal);
        Assert.Contains("btn-danger", cut.Find("[data-testid=node-reject-confirm]").ClassName, StringComparison.Ordinal);
        cut.ClickAt("[data-testid=node-reject-confirm]");

        cut.WaitForAssertion(() => Assert.Contains("rejected", Text(cut, "nodes-status"), StringComparison.Ordinal), Wait);
        Assert.Null(await Nodes.GetAsync("pc-office"));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=nodes-row-pc-office]")), Wait);
    }

    [Fact]
    public async Task RowMenu_DrainThenUndrain_SetsTheFlag_AndAnOfflineDrainingNodeShowsBoth()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office"));
        var cut = RenderList();
        Assert.Equal("offline", cut.Find("[data-testid=nodes-state-pc-office]").GetAttribute("data-state"));

        cut.ClickAt("[data-testid=nodes-drain-pc-office]");
        Assert.Contains("No new model starts or placements", Text(cut, "node-drain"), StringComparison.Ordinal);
        cut.ClickAt("[data-testid=node-drain-confirm]");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=nodes-draining-pc-office]")), Wait);
        Assert.True((await Nodes.GetAsync("pc-office"))!.Draining);
        // Offline and draining: the Offline badge and a Draining chip, not one or the other.
        Assert.Equal("offline", cut.Find("[data-testid=nodes-state-pc-office]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=nodes-undrain-pc-office]"));

        cut.ClickAt("[data-testid=nodes-undrain-pc-office]");
        cut.ClickAt("[data-testid=node-undrain-confirm]");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=nodes-draining-pc-office]")), Wait);
        Assert.False((await Nodes.GetAsync("pc-office"))!.Draining);
        var actions = (await Audit.ReadAsync(new AuditQuery { NodeId = "pc-office" })).Select(l => l.Action).ToList();
        Assert.Contains(AuditActions.NodeDrain, actions);
        Assert.Contains(AuditActions.NodeUndrain, actions);
    }

    [Fact]
    public async Task DegradedAndDraining_ShowsTheDegradedBadgeAndADrainingChip_OnlineAndDrainingReadsDraining()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office") with { Draining = true });
        await Nodes.UpsertAsync(Approved("pc-lab") with { Draining = true });
        _cache.Set(
            Live("pc-office", NodeState.Degraded) with { Draining = true, Reasons = ["GPU 1 is not answering."] },
            Live("pc-lab", NodeState.Online) with { Draining = true });

        var cut = RenderList();

        Assert.Equal("degraded", cut.Find("[data-testid=nodes-state-pc-office]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=nodes-draining-pc-office]"));
        Assert.Equal("draining", cut.Find("[data-testid=nodes-state-pc-lab]").GetAttribute("data-state"));
        Assert.Empty(cut.FindAll("[data-testid=nodes-draining-pc-lab]"));
    }

    [Fact]
    public async Task RowMenu_Revoke_NeedsTheTypedIdAndUnplaceChoice_ThenUnplacesAndRevokes()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office"));
        await Models.UpsertAsync(Placed("qwen", "pc-office"));
        await Models.UpsertAsync(Placed("gemma", "pc-office"));
        await Models.UpsertAsync(Placed("other", "dgx"));
        var cut = RenderList();

        cut.ClickAt("[data-testid=nodes-revoke-pc-office]");
        string consequence = Text(cut, "node-revoke-consequence");
        Assert.Contains("Revoke node pc-office?", consequence, StringComparison.Ordinal);
        Assert.Contains("2 models placed there become unplaced and stop serving.", consequence, StringComparison.Ordinal);
        Assert.Contains("The agent keeps running but the master will no longer talk to it.", consequence, StringComparison.Ordinal);
        Assert.Contains("gemma, qwen", cut.Find("[data-testid=node-revoke]").TextContent, StringComparison.Ordinal);
        var confirm = () => cut.Find("[data-testid=node-revoke-confirm]");
        Assert.Contains("btn-danger-fill", confirm().GetAttribute("class"), StringComparison.Ordinal);
        Assert.True(confirm().HasAttribute("disabled"));

        InputAt(cut, "[data-testid=node-revoke-typed]", "pc-office");
        Assert.True(confirm().HasAttribute("disabled")); // the placed models must be unplaced on purpose
        cut.ChangeAt("[data-testid=node-revoke-unplace]", true);
        Assert.False(confirm().HasAttribute("disabled"));

        cut.ClickAt("[data-testid=node-revoke-confirm]");

        cut.WaitForAssertion(() => Assert.Contains("unplaced gemma, qwen", Text(cut, "nodes-status"), StringComparison.Ordinal), Wait);
        Assert.True((await Nodes.GetAsync("pc-office"))!.Revoked);
        Assert.Null((await Models.GetAsync("qwen"))!.Placement);
        Assert.Null((await Models.GetAsync("gemma"))!.Placement);
        Assert.Equal("dgx", (await Models.GetAsync("other"))!.Placement!.NodeId);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=nodes-row-pc-office]")), Wait);
        var revokes = (await Audit.ReadAsync(new AuditQuery { NodeId = "pc-office" })).Where(l => l.Action == AuditActions.NodeRevoke).ToList();
        Assert.DoesNotContain(revokes, l => l.Result == AuditResults.Refused);
        Assert.Contains(revokes, l => l.Result == AuditResults.Succeeded);
    }

    [Fact]
    public async Task RowMenu_Rekey_ShowsTheUidAndTheFullCommand_AndAPendingNodeStillNeedsApproval()
    {
        Setup();
        await PendingAsync("pc-office");
        string uid = (await Nodes.GetAsync("pc-office"))!.Uid;
        var cut = RenderList();

        cut.ClickAt("[data-testid=nodes-rekey-pc-office]");
        Assert.Contains(uid, cut.Find("[data-testid=node-rekey]").TextContent, StringComparison.Ordinal);
        Assert.False(cut.Find("[data-testid=node-rekey-confirm]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=node-rekey-password]"));
        cut.ChangeAt("[data-testid=node-rekey-ttl]", "10");
        cut.ClickAt("[data-testid=node-rekey-confirm]");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=node-rekey-command]")), Wait);
        Assert.Equal(uid, Text(cut, "node-rekey-command-uid"));
        string prompt = Text(cut, "node-rekey-command-linux-prompt");
        Assert.StartsWith($"sudo ./install-agent.sh --rekey --node-id pc-office --node-uid {uid} ", prompt, StringComparison.Ordinal);
        string token = Text(cut, "node-rekey-command-token");
        cut.ChangeAt("[data-testid=node-rekey-command-mode-inline]", true);
        Assert.Contains($"MIRBADLLM_JOIN_TOKEN='{token}'", Text(cut, "node-rekey-command-linux"), StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=node-rekey-pending-note]"));
        Assert.Contains(await Nodes.ListJoinTokensAsync(), t => t.NodeId == "pc-office" && t.Purpose == JoinTokenPurpose.Rekey);

        // Closing drops the token from the page.
        cut.ClickAt("[data-testid=node-rekey-cancel]");
        Assert.DoesNotContain(token, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowMenu_Rekey_OfTheDgxContainerAgent_ShowsTheContainerProcedureNotTheInstaller()
    {
        // MN-124 (D105): the bootstrap node runs its agent in a container; install-agent.sh would add a second agent.
        Setup();
        await Nodes.UpsertAsync(Approved("dgx"));
        _cache.Set(Live("dgx", NodeState.Offline));
        string uid = (await Nodes.GetAsync("dgx"))!.Uid;
        var cut = RenderList();

        cut.ClickAt("[data-testid=nodes-rekey-dgx]");
        cut.ClickAt("[data-testid=node-rekey-confirm]");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=node-rekey-container]")), Wait);
        string script = Text(cut, "node-rekey-container");
        string token = Text(cut, "node-rekey-container-token");
        Assert.Contains($"--node-id 'dgx' --node-uid '{uid}'", script, StringComparison.Ordinal);
        Assert.Contains("agent join --rekey --enroll-url https://mirbadllm:8443", script, StringComparison.Ordinal);
        Assert.DoesNotContain(token, script, StringComparison.Ordinal);
        Assert.DoesNotContain("install-agent.sh --rekey", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=node-rekey-command]"));

        cut.ClickAt("[data-testid=node-rekey-cancel]");
        Assert.DoesNotContain(token, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowMenu_Rekey_WithAgentBundles_DownloadsAndRunsTheRekeyVerb_TheDgxKeepsTheContainerProcedure()
    {
        Setup();
        string bundles = Path.Combine(_root, "bundles");
        string x64 = AgentBundleFixtures.Write(bundles, "linux-x64");
        AgentBundleFixtures.Write(bundles, "linux-arm64");
        _ctx.Services.Configure<NodeApiOptions>(o => o.AgentBundleDirectory = bundles);
        await PendingAsync("pc-office");
        await Nodes.UpsertAsync(Approved("dgx"));
        _cache.Set(Live("dgx", NodeState.Offline));
        string uid = (await Nodes.GetAsync("pc-office"))!.Uid;
        var cut = RenderList();

        cut.ClickAt("[data-testid=nodes-rekey-pc-office]");
        cut.ClickAt("[data-testid=node-rekey-confirm]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=node-rekey-command]")), Wait);
        string prompt = Text(cut, "node-rekey-command-linux-prompt");
        string token = Text(cut, "node-rekey-command-token");
        Assert.Contains($"h={x64} ;;", prompt, StringComparison.Ordinal);
        Assert.Contains($"./mirbadllm-agent-*/install-agent.sh rekey --node-id pc-office --node-uid {uid} --enroll-url https://", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(token, prompt, StringComparison.Ordinal);
        cut.ClickAt("[data-testid=node-rekey-cancel]");

        // The DGX's agent is a container: its rekey stays `docker compose run … join --rekey`, bundles or not.
        cut.ClickAt("[data-testid=nodes-rekey-dgx]");
        cut.ClickAt("[data-testid=node-rekey-confirm]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=node-rekey-container]")), Wait);
        Assert.Contains("agent join --rekey --enroll-url https://mirbadllm:8443", Text(cut, "node-rekey-container"), StringComparison.Ordinal);
        Assert.DoesNotContain("agent/bundles", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=node-rekey-command]"));
    }

    [Fact]
    public async Task RowMenu_ApprovedNode_OffersDrainRekeyAndRevoke_RotateOnlyWithTheRotationService()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office"));
        _cache.Set(Live("pc-office", NodeState.Online));
        var cut = RenderList();

        var menu = cut.Find("[data-testid=nodes-more-pc-office-menu]");
        Assert.NotNull(menu.QuerySelector("[data-testid=nodes-drain-pc-office]"));
        Assert.NotNull(menu.QuerySelector("[data-testid=nodes-rekey-pc-office]"));
        Assert.NotNull(menu.QuerySelector("[data-testid=nodes-revoke-pc-office]"));
        Assert.Null(menu.QuerySelector("[data-testid=nodes-approve-pc-office]"));
        Assert.Null(menu.QuerySelector("[data-testid=nodes-reject-pc-office]"));
        // No CertificateRotationService in this master: no rotate item.
        Assert.Null(menu.QuerySelector("[data-testid=nodes-rotate-pc-office]"));
        Assert.All(menu.QuerySelectorAll("button"), b => Assert.Equal("menuitem", b.GetAttribute("role")));
    }

    // ---------- Single DGX ----------

    [Fact]
    public async Task OnlyTheDgx_LooksClean()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("dgx") with { DisplayName = "DGX Spark" });
        await Models.UpsertAsync(new ModelDefinition { Id = "qwen", ServedName = "qwen3-coder", ModelPath = "q", MemoryBudgetGiB = 40 });
        _cache.Set(Dgx());

        var list = RenderList();

        Assert.Single(list.FindAll("[data-testid=nodes-table] > article"));
        Assert.Equal("online", list.Find("[data-testid=nodes-state-dgx]").GetAttribute("data-state"));
        Assert.Empty(list.FindAll("[data-testid=nodes-draining-dgx]"));
        Assert.Empty(list.FindAll("[data-testid=nodes-job-dgx]"));
        Assert.Empty(list.FindAll("[data-testid=nodes-pager]"));
        Assert.Empty(list.FindAll("[data-testid=nodes-status]"));
        Assert.Empty(list.FindAll("[data-testid=nodes-error]"));
        Assert.NotNull(list.Find("[data-testid=nodes-add]"));
        Assert.DoesNotContain("NaN", list.Markup, StringComparison.Ordinal);

        var detail = RenderDetail("dgx");

        Assert.Empty(detail.FindAll("[data-testid=node-fencing]"));
        Assert.Empty(detail.FindAll("[data-testid=node-draining]"));
        Assert.Empty(detail.FindAll("[data-testid=node-job]"));
        Assert.Empty(detail.FindAll("[data-testid=node-ca-pending]"));
        Assert.Empty(detail.FindAll("[data-testid=node-approve-open]"));
        Assert.NotNull(detail.Find("[data-testid=node-attention-none]"));
        Assert.Equal(
            ["overview", "gpus", "services", "operations", "settings"],
            detail.FindAll("[data-testid=node-tabs-list] [role=tab]").Select(t => t.GetAttribute("data-testid")!["node-tabs-tab-".Length..]).ToArray());
        Assert.Equal("1", detail.Find("[data-testid=node-tabs-tab-gpus-count]").TextContent);
        Assert.Empty(detail.FindAll("[data-testid=node-tabs-tab-settings-badge]"));

        // The DGX's models have no placement (P1 definitions) but run there: Overview lists them.
        Assert.NotNull(detail.Find("[data-testid=node-model-qwen]"));
        Assert.Equal("ready", detail.Find("[data-testid=node-model-state-qwen]").GetAttribute("data-state"));
        Assert.Contains("Shared", detail.Find("[data-testid=node-model-qwen]").TextContent, StringComparison.Ordinal);

        detail.ClickAt("[data-testid=node-tabs-tab-gpus]");
        Assert.Contains("Shares host memory", detail.Find("[data-testid=node-gpu-0]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(detail.Find("[data-testid=node-gpus-unified]"));
        Assert.Equal("—", Text(detail, "node-gpu-0-util"));
        Assert.DoesNotContain("NaN", detail.Markup, StringComparison.Ordinal);
    }

    // ---------- Detail tabs ----------

    [Fact]
    public async Task Detail_GpusTab_ShowsEveryGpuWithVramReserveAndUtilization()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office") with { GpuReserveGiB = new Dictionary<string, double> { ["GPU-a"] = 2 } });
        _cache.Set(Live("pc-office", NodeState.Online) with
        {
            Capabilities = PcCaps(),
            Memory = new NodeMemorySnapshot
            {
                Model = MemoryModel.Discrete,
                HostTotalGiB = 64,
                HostAvailableGiB = 40,
                Gpus = [new GpuMemorySnapshot(0, "GPU-a", 16, 4, 12), new GpuMemorySnapshot(1, "GPU-b", 16, null, null)],
            },
        });
        _history.HostSamples =
        [
            new HostSample(_time.GetUtcNow(), new MemorySnapshot(64, 40), null)
            {
                Gpus = [new HostGpuSample(0, "GPU-a", 37, 4, 16), new HostGpuSample(1, "GPU-b", null, null, 16)],
            },
        ];
        Navigate("nodes/pc-office?tab=gpus");

        var cut = RenderDetail("pc-office");

        var gpu0 = cut.Find("[data-testid=node-gpu-0]");
        Assert.Contains("NVIDIA GeForce RTX 4060 Ti", gpu0.TextContent, StringComparison.Ordinal);
        Assert.Contains("GPU-a", gpu0.TextContent, StringComparison.Ordinal);
        Assert.Contains("12.0 GiB", gpu0.TextContent, StringComparison.Ordinal);
        Assert.Contains("2.0 GiB", gpu0.TextContent, StringComparison.Ordinal);
        Assert.Equal("37%", Text(cut, "node-gpu-0-util"));
        Assert.Equal("—", Text(cut, "node-gpu-1-util"));
        Assert.Contains("—", cut.Find("[data-testid=node-gpu-1]").TextContent, StringComparison.Ordinal);
        // The per-GPU headroom bars are Overview's; this tab is the per-GPU detail.
        Assert.Empty(cut.FindAll("[data-testid=node-memory-gpu-0]"));
        Assert.Equal("2", cut.Find("[data-testid=node-tabs-tab-gpus-count]").TextContent);
    }

    [Fact]
    public async Task Detail_GpusTab_OnAMac_ShowsTheAppleGpuFromTelemetry_AsSharingHostMemory()
    {
        // macOS Phase 4 (M8): a Mac reports no GPU in its capabilities (placement must not offer a GPU choice), but samples
        // its GPU's utilisation from IOKit; the tab shows that one GPU, named after the chip, with no power or VRAM.
        Setup();
        await Nodes.UpsertAsync(Approved("mac-1"));
        _cache.Set(Live("mac-1", NodeState.Online) with
        {
            Capabilities = PcCaps() with
            {
                Os = new NodeOsInfo("macos", "macOS 27.0.1"),
                Arch = "arm64",
                Cpu = new NodeCpuInfo(8, "Apple M2"),
                Memory = new NodeMemoryInfo(MemoryModel.Unified, 8),
                Gpus = [],
            },
        });
        _history.HostSamples = [new HostSample(_time.GetUtcNow(), new MemorySnapshot(8, 3), new GpuSnapshot(42, null, null, null))];
        Navigate("nodes/mac-1?tab=gpus");

        var cut = RenderDetail("mac-1");

        var gpu = cut.Find("[data-testid=node-gpu-0]");
        Assert.Contains("Apple M2 GPU", gpu.TextContent, StringComparison.Ordinal);
        Assert.Contains("Shares host memory", gpu.TextContent, StringComparison.Ordinal);
        Assert.Contains("IOKit", gpu.TextContent, StringComparison.Ordinal);
        Assert.Equal("42%", Text(cut, "node-gpu-0-util"));
        Assert.NotNull(cut.Find("[data-testid=node-gpus-unified]"));
        Assert.Equal("1", cut.Find("[data-testid=node-tabs-tab-gpus-count]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=node-gpus-empty]"));
    }

    [Fact]
    public async Task Detail_GpusTab_OnAMacWithoutTelemetryYet_SaysItHasNoGpuReading()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("mac-1"));
        _cache.Set(Live("mac-1", NodeState.Online) with
        {
            Capabilities = PcCaps() with { Os = new NodeOsInfo("macos", "macOS 27.0.1"), Memory = new NodeMemoryInfo(MemoryModel.Unified, 8), Gpus = [] },
        });
        Navigate("nodes/mac-1?tab=gpus");

        var cut = RenderDetail("mac-1");

        Assert.NotNull(cut.Find("[data-testid=node-gpus-empty]"));
        Assert.Empty(cut.FindAll("[data-testid=node-gpu-0]"));
    }

    [Fact]
    public async Task Detail_AnOldModelsLink_OpensOverview_WhichListsThePlacedModelsWithStateBudgetAndGpus()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office"));
        await Models.UpsertAsync(Placed("qwen", "pc-office") with { Placement = new ModelPlacement("pc-office", GpuUuids: ["GPU-b"]) });
        await Models.UpsertAsync(Placed("gemma", "pc-office"));
        await Models.UpsertAsync(Placed("other", "dgx"));
        _cache.Set(Live("pc-office", NodeState.Online) with
        {
            Capabilities = PcCaps(),
            Runtimes = [new NodeModelRuntime { ModelId = "qwen", NodeId = "pc-office", State = ModelState.Ready }],
        });
        Navigate("nodes/pc-office?tab=models");

        var cut = RenderDetail("pc-office");

        Assert.Equal(["node-model-gemma", "node-model-qwen"], cut.FindAll("[data-testid=node-models-table] tbody tr").Select(r => r.GetAttribute("data-testid")).ToArray());
        Assert.Equal("ready", cut.Find("[data-testid=node-model-state-qwen]").GetAttribute("data-state"));
        Assert.Equal("—", Text(cut, "node-model-state-gemma"));
        var qwen = cut.Find("[data-testid=node-model-qwen]");
        Assert.Contains("20.0 GiB", qwen.TextContent, StringComparison.Ordinal);
        Assert.Contains("1", qwen.QuerySelector("[data-label=GPUs]")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("models/qwen", cut.Find("[data-testid=node-model-open-qwen]").GetAttribute("href"));
        Assert.Equal("true", cut.Find("[data-testid=node-tabs-tab-overview]").GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task Detail_RecentCommands_ReadTheAuditLogForThisNode_NewestFirst()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office"));
        await Nodes.UpsertAsync(Approved("pc-lab"));
        await Admin.SetDrainingAsync("pc-office", true, TestCaller);
        _time.Advance(TimeSpan.FromMinutes(2));
        await Admin.SetDrainingAsync("pc-office", false, new NodeApiCaller("admin:0123456789ab", "x"));
        await Admin.SetDrainingAsync("pc-lab", true, TestCaller);
        _time.Advance(TimeSpan.FromMinutes(1));

        var cut = RenderDetail("pc-office");

        // Operations holds them, under what runs now; Overview no longer repeats them.
        Assert.Empty(cut.FindAll("[data-testid=node-audit-table]"));
        cut.ClickAt("[data-testid=node-tabs-tab-operations]");

        var rows = cut.FindAll("[data-testid=node-audit-table] tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Undrain", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Succeeded", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Admin (session 012345)", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("1m 00s ago", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("not draining", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Drain", rows[1].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_SettingsTab_ShowsTheAgentCertificateAndThisNodesCaRotationProgress_FromAnOldCertificatesLink()
    {
        Setup();
        string pin = Pin();
        await Nodes.UpsertAsync(Approved("pc-office", pin) with
        {
            CertNotAfter = _time.GetUtcNow().AddDays(60),
            Rotation = new CertificateRotationRecord { RotationId = "rot-1", NodeUid = "pc-office-uid", Stage = CertificateRotationStage.CertIssued, Error = "agent busy", UpdatedAt = _time.GetUtcNow() },
        });
        string pkiDir = Path.Combine(_root, "pki");
        Directory.CreateDirectory(pkiDir);
        var trust = MasterTrustStore.Load(pkiDir, _ca.Certificate, _time);
        await trust.UpdateAsync(d => d with
        {
            Rotation = new CaRotationRecord
            {
                RotationId = "ca-1",
                OldCaSpkiSha256 = _ca.SpkiSha256,
                NewCaSpkiSha256 = _ca.SpkiSha256,
                Stage = CaRotationStage.AgentsRotating,
                StartedAt = _time.GetUtcNow(),
                Nodes = [new CaRotationNodeProgress { NodeId = "pc-office", NodeUid = "pc-office-uid", TrustAdded = true, Error = "timed out" }],
            },
        });
        _ctx.Services.AddSingleton(trust);
        Navigate("nodes/pc-office?tab=certificates");

        var cut = RenderDetail("pc-office");

        Assert.Contains("(in 60 days)", Text(cut, "node-certs-expiry"), StringComparison.Ordinal);
        Assert.Equal(Spki.ToDisplayFingerprint(pin), Text(cut, "node-certs-pin"));
        Assert.Equal("Rotating: certificate issued", Text(cut, "node-certs-rotation"));
        Assert.Equal("agent busy", Text(cut, "node-certs-error"));
        cut.ClickAt("[data-testid=node-settings-tabs-tab-ca-rotation]");
        Assert.Equal("Rotating the agent certificates", Text(cut, "node-ca-stage"));
        Assert.Equal("Trusts the new CA; certificate not rotated yet", Text(cut, "node-ca-progress"));
        Assert.Equal("timed out", Text(cut, "node-ca-error"));
        Assert.Contains("delete them once it closes", Text(cut, "node-ca-backups"), StringComparison.Ordinal);
        Assert.Equal("CA rotation", cut.Find("[data-testid=node-tabs-tab-settings-badge]").TextContent.Trim());
        Assert.Equal("true", cut.Find("[data-testid=node-tabs-tab-settings]").GetAttribute("aria-selected"));
        // Re-pair and rotate are the header's actions now; no rotation service registered, so no rotate button.
        Assert.NotNull(cut.Find("[data-testid=node-rekey-pc-office]"));
        Assert.Empty(cut.FindAll("[data-testid=node-rotate-pc-office]"));
        trust.Dispose();
    }

    [Fact]
    public async Task Detail_SettingsTab_PatchesOnlyTheChangedFields_AndShowsRefusalsInPlace()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office") with { DisplayName = "Office PC", Labels = ["x64"], MemoryReserveGiB = 4 });
        _cache.Set(Live("pc-office", NodeState.Online) with { Capabilities = PcCaps() });
        Navigate("nodes/pc-office?tab=settings");
        var cut = RenderDetail("pc-office");

        Assert.Equal("Office PC", cut.Find("[data-testid=node-settings-name]").GetAttribute("value"));
        Assert.Empty(cut.FindAll("[data-testid=node-settings-save]"));
        Assert.Equal("4", cut.Find("[data-testid=node-settings-reserve]").GetAttribute("value"));

        // An invalid digest: the service refuses, the message shows under the form, nothing is written.
        InputAt(cut, "[data-testid=node-settings-vllm-digest]", "sha256:nope");
        Assert.NotNull(cut.Find("[data-testid=node-settings-save]"));
        Submit(cut);
        Assert.Contains("repository digest", Text(cut, "node-settings-error"), StringComparison.Ordinal);
        Assert.Null((await Nodes.GetAsync("pc-office"))!.DefaultVllmImageDigest);

        InputAt(cut, "[data-testid=node-settings-vllm-digest]", "");
        InputAt(cut, "[data-testid=node-settings-name]", "Office workstation");
        InputAt(cut, "[data-testid=node-settings-reserve]", "6.5");
        InputAt(cut, "[data-testid=node-settings-gpu-0]", "1.5");
        InputAt(cut, "[data-testid=node-settings-labels]", "x64 office");
        cut.ClickAt("[data-testid=node-settings-tabs-tab-certificate]");
        Assert.Empty(cut.FindAll("[data-testid=node-settings-form]"));
        cut.ClickAt("[data-testid=node-settings-tabs-tab-general]");
        Assert.Equal("Office workstation", cut.Find("[data-testid=node-settings-name]").GetAttribute("value"));
        Assert.Equal("6.5", cut.Find("[data-testid=node-settings-reserve]").GetAttribute("value"));
        Submit(cut);

        cut.WaitForAssertion(() => Assert.Contains("Saved the settings", Text(cut, "node-status"), StringComparison.Ordinal), Wait);
        NodeRecord record = (await Nodes.GetAsync("pc-office"))!;
        Assert.Equal("Office workstation", record.DisplayName);
        Assert.Empty(cut.FindAll("[data-testid=node-settings-save]"));
        Assert.Equal(6.5, record.MemoryReserveGiB);
        Assert.Equal(1.5, record.GpuReserveGiB["GPU-a"]);
        Assert.False(record.GpuReserveGiB.ContainsKey("GPU-b"));
        Assert.Equal(["x64", "office"], record.Labels);
        var update = (await Audit.ReadAsync(new AuditQuery { NodeId = "pc-office" })).First(l => l.Action == AuditActions.NodeUpdate && l.Result == AuditResults.Succeeded);
        Assert.Equal("changed displayName, memoryReserveGiB, gpuReserveGiB, labels", update.Detail);

        // Saving again without a change writes nothing.
        int lines = (await Audit.ReadAsync(new AuditQuery { NodeId = "pc-office" })).Count;
        Submit(cut);
        Assert.Contains("Nothing to save", Text(cut, "node-status"), StringComparison.Ordinal);
        Assert.Equal(lines, (await Audit.ReadAsync(new AuditQuery { NodeId = "pc-office" })).Count);
    }

    [Fact]
    public async Task Detail_PendingNode_HeaderOffersApprove_AndTheOverviewSaysNothingIsSentYet()
    {
        Setup();
        string pin = await PendingAsync("pc-office");
        var cut = RenderDetail("pc-office");

        Assert.Contains("waits for approval", Text(cut, "node-pending-note"), StringComparison.Ordinal);
        cut.ClickAt("[data-testid=node-approve-open]");
        Assert.Equal(Spki.ToDisplayFingerprint(pin), Text(cut, "node-approve-facts-fingerprint"));
        cut.ClickAt("[data-testid=node-approve-confirm]");

        cut.WaitForAssertion(() => Assert.Contains("is approved", Text(cut, "node-status"), StringComparison.Ordinal), Wait);
        Assert.True((await Nodes.GetAsync("pc-office"))!.Approved);
        Assert.Empty(cut.FindAll("[data-testid=node-approve-open]"));
    }

    [Fact]
    public async Task Detail_FencingState_IsExplained_AndCurrentSaysNothing()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office"));
        await Nodes.UpsertAsync(Approved("dgx"));
        _ctx.Services.AddSingleton<IFencingCoordinator>(new FakeFencing(
            new NodeFencingStatus("pc-office", NodeFencingState.NeedsContinuation, 9),
            new NodeFencingStatus("dgx", NodeFencingState.Current, 3)));

        var pc = RenderDetail("pc-office");
        var dgx = RenderDetail("dgx");

        Assert.Contains("returned holding epoch 9", Text(pc, "node-fencing"), StringComparison.Ordinal);
        Assert.Empty(dgx.FindAll("[data-testid=node-fencing]"));
    }

    [Fact]
    public async Task Detail_OperationsTab_ListsOpenOperations_WithoutAJobPanelWhenNoJobRuns()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office"));
        _cache.Set(Live("pc-office", NodeState.Online) with
        {
            OpenOperations = [new NodeOperationSummary("op-7", "files.download", "Qwen3-8B", OperationState.Running, "downloading", _time.GetUtcNow().AddSeconds(-90))],
        });
        Navigate("nodes/pc-office?tab=operations");

        var cut = RenderDetail("pc-office");

        Assert.Empty(cut.FindAll("[data-testid=node-job-panel]")); // no benchmark or tuning job here
        Assert.Contains("1m 30s", cut.Find("[data-testid=node-op-op-7]").TextContent, StringComparison.Ordinal);
        Assert.Equal("1", cut.Find("[data-testid=node-tabs-tab-operations-count]").TextContent);
    }

    [Fact]
    public void AddNode_WithAgentBundles_TheCommandDownloadsAndChecksTheAgent_AndThePromptHasNoToken()
    {
        Setup();
        string bundles = Path.Combine(_root, "bundles");
        string x64 = AgentBundleFixtures.Write(bundles, "linux-x64", version: "0123456789ab");
        string arm = AgentBundleFixtures.Write(bundles, "linux-arm64", version: "0123456789ab");
        _ctx.Services.Configure<NodeApiOptions>(o => o.AgentBundleDirectory = bundles);
        var cut = RenderList();

        OpenAddNode(cut);
        InputAt(cut, "[data-testid=add-node-id]", "pc-office");
        cut.ClickAt("[data-testid=add-node-confirm]");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=add-node-command]")), Wait);
        string token = Text(cut, "add-node-command-token");
        string prompt = Text(cut, "add-node-command-linux-prompt");
        // The page's own address (bUnit: http://localhost/) is where the PC downloads from; each architecture's hash is in.
        Assert.StartsWith("(set -e; ", prompt, StringComparison.Ordinal);
        Assert.Contains($"x86_64|amd64) u='http://localhost/agent/bundles/linux-x64.tar.gz' h={x64} ;;", prompt, StringComparison.Ordinal);
        Assert.Contains($"aarch64|arm64) u='http://localhost/agent/bundles/linux-arm64.tar.gz' h={arm} ;;", prompt, StringComparison.Ordinal);
        Assert.Contains("sudo --preserve-env=MIRBADLLM_JOIN_TOKEN ./mirbadllm-agent-*/install-agent.sh install --node-id pc-office --enroll-url https://", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(token, prompt, StringComparison.Ordinal);
        Assert.Contains("downloads the agent", Text(cut, "add-node-command-prompt-note"), StringComparison.Ordinal);
        Assert.DoesNotContain("in the folder with", Text(cut, "add-node-command-prompt-note"), StringComparison.Ordinal);
        Assert.Contains(x64, Text(cut, "add-node-command-agent"), StringComparison.Ordinal);
        Assert.Contains("0123456789ab", Text(cut, "add-node-command-agent"), StringComparison.Ordinal);

        cut.ChangeAt("[data-testid=add-node-command-mode-inline]", true);
        string inline = Text(cut, "add-node-command-linux");
        Assert.Equal(prompt.Replace("sudo --preserve-env=MIRBADLLM_JOIN_TOKEN ", $"sudo MIRBADLLM_JOIN_TOKEN='{token}' ", StringComparison.Ordinal), inline);
        Assert.Contains("downloads the agent", Text(cut, "add-node-command-inline-note"), StringComparison.Ordinal);
    }

    [Fact]
    public void AddNode_WithoutAgentBundles_KeepsTheRunInTheFolderText()
    {
        Setup();
        var cut = RenderList();

        OpenAddNode(cut);
        InputAt(cut, "[data-testid=add-node-id]", "pc-office");
        cut.ClickAt("[data-testid=add-node-confirm]");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=add-node-command]")), Wait);
        Assert.Contains("in the folder with install-agent.sh", Text(cut, "add-node-command-prompt-note"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=add-node-command-agent]"));
    }

    // ---------- Pieces ----------

    [Fact]
    public void PromptVariant_IsBuiltFromTheTokensParts_ForAJoinAndARekey()
    {
        // W8 review (MN-128): both forms come from one builder (LinuxInstallerLine), not from cutting the token out.
        var join = Issued(JoinTokenPurpose.Join, LinuxInstallerLine.Join("pc-1", "https://m:8443", "sha256:x").WithToken("abc_DEF-123"));
        Assert.Equal("sudo MIRBADLLM_JOIN_TOKEN='abc_DEF-123' ./install-agent.sh --node-id pc-1 --enroll-url https://m:8443 --ca-hash sha256:x",
            join.Commands.Linux);
        Assert.Equal("sudo ./install-agent.sh --node-id pc-1 --enroll-url https://m:8443 --ca-hash sha256:x", JoinCommandView.PromptVariant(join, null));

        var rekey = Issued(JoinTokenPurpose.Rekey, LinuxInstallerLine.Rekey("pc-1", "u1", "https://m:8443", "sha256:x").WithToken("abc_DEF-123"));
        Assert.Equal("sudo ./install-agent.sh --rekey --node-id pc-1 --node-uid u1 --enroll-url https://m:8443 --ca-hash sha256:x",
            JoinCommandView.PromptVariant(rekey, "u1"));
        Assert.DoesNotContain("abc_DEF-123", JoinCommandView.PromptVariant(rekey, "u1"), StringComparison.Ordinal);

        // A line this page cannot rebuild exactly gets no prompt form (only the master's own line is shown).
        Assert.Null(JoinCommandView.PromptVariant(rekey, null));
        Assert.Null(JoinCommandView.PromptVariant(rekey, "another-uid"));
        Assert.Null(JoinCommandView.PromptVariant(join with { Commands = new JoinCommands("./install-agent.sh --node-id pc-1") }, null));
        Assert.Null(JoinCommandView.PromptVariant(join with { CaHash = "sha256:y" }, null));

        static JoinTokenCreateResponse Issued(JoinTokenPurpose purpose, string linux) => new()
        {
            NodeId = "pc-1",
            Token = "abc_DEF-123",
            CaHash = "sha256:x",
            EnrollUrl = "https://m:8443",
            ExpiresAt = DateTimeOffset.UnixEpoch,
            Purpose = purpose,
            Commands = new JoinCommands(linux),
        };
    }

    [Fact]
    public async Task Caller_FromTheSignedInCircuitUser_UsesTheSessionHashOfItsSid()
    {
        Setup();
        var auth = _auth!;
        auth.SetAuthorized("admin");
        auth.SetClaims(new Claim(ClaimTypes.Sid, "sid-123"));

        NodeApiCaller caller = await NodeActionDialogs.ResolveCallerAsync(_ctx.Services, "spark.lan");

        Assert.Equal(AuditActors.Admin(AuditActorResolver.SessionHash("sid-123")), caller.Actor);
        Assert.Equal("spark.lan", caller.RequestHost);
    }

    [Fact]
    public async Task AgentUpdate_ViewOperationsSelectsTheTabAndPersistsItsUrl()
    {
        Setup();
        await Nodes.UpsertAsync(Approved("pc-office"));
        _cache.Set(Live("pc-office", NodeState.Degraded) with
        {
            AgentVersion = "1.0.0+111111111111",
            OpenOperations = [new NodeOperationSummary("update-1", OperationKinds.AgentUpdate, "target-build",
                OperationState.Running, OperationStages.Downloading, _time.GetUtcNow())],
        });
        Navigate("/nodes/pc-office");
        var cut = RenderDetail("pc-office");

        cut.ClickAt("[data-testid=agent-update-operations]");

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=node-tabs-tab-operations]").GetAttribute("aria-selected")), Wait);
        Assert.Contains("tab=operations", _ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    // ---------- Harness ----------

    private void Setup(bool loginEnabled = true)
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _auth = _ctx.AddAuthorization();
        _auth.SetAuthorized("admin");
        _auth.SetClaims(TestAdmin.User.Claims.ToArray()); // a circuit without a signed-in user unless a test signs one in
        _module = _ctx.JSInterop.SetupModule("./interop.js");
        _module.Setup<bool>("copyText", _ => true).SetResult(true);
        var services = _ctx.Services;
        services.AddLogging();
        services.AddOptions();
        services.Configure<MirbadLLMOptions>(o => o.DataPath = DataDir);
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<INodeRegistry>(_ => (INodeRegistry?)_gatedNodes ?? Nodes);
        services.AddSingleton<IModelRegistry>(_ => Models);
        services.AddSingleton<IAuditLog>(_ => Audit);
        services.AddSingleton(_ca);
        services.AddSingleton<INodeStatusCache>(_cache);
        services.AddSingleton<IMetricsHistory>(_history);
        services.AddOptions<AuthOptions>().Configure(o => o.AdminPassword = loginEnabled ? Password : null);
        services.AddMirbadLLMAdminReauth();
        services.AddMirbadLLMNodeApi(new ConfigurationBuilder().Build());
    }

    /// <summary>Renders the list and waits for its first read (the real registries read files asynchronously).</summary>
    private IRenderedComponent<Nodes> RenderList()
    {
        var cut = _ctx.Render<Nodes>();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=nodes-loading]")), Wait);
        return cut;
    }

    /// <summary>Opens the Add node dialog and waits for its read of the registered nodes (the id check needs it).</summary>
    private static void OpenAddNode(IRenderedComponent<Nodes> cut)
    {
        cut.ClickAt("[data-testid=nodes-add]");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=add-node-id-checking]")), Wait);
    }

    /// <summary>Renders a node's detail page and waits for its first read.</summary>
    private IRenderedComponent<NodeDetail> RenderDetail(string id)
    {
        var cut = _ctx.Render<NodeDetail>(p => p.Add(x => x.Id, id));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=node-loading]")), Wait);
        return cut;
    }

    private void Navigate(string uri) => _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);

    /// <summary>Mints a join token through the service and enrolls through the real enrollment service; returns the pending pin.</summary>
    private async Task<string> PendingAsync(string nodeId)
    {
        var issued = await Admin.CreateJoinTokenAsync(new JoinTokenCreateRequest { NodeId = nodeId, Password = Password }, TestCaller);
        return await EnrollAsync(issued.Token, nodeId);
    }

    private async Task<string> EnrollAsync(string token, string nodeId)
    {
        using var service = new EnrollmentService(
            Nodes,
            _ca,
            Options.Create(new EnrollmentOptions()),
            Options.Create(new RegistryMigrationOptions()),
            _time,
            NullLogger<EnrollmentService>.Instance);
        using ECDsa key = PkiDefaults.CreateKey();
        EnrollmentResult result = await service.EnrollAsync(new EnrollRequest
        {
            Token = token,
            NodeId = nodeId,
            CsrPem = CertificateSigningRequests.CreatePem(key, nodeId),
            Addresses = [$"https://{AgentIp}:9443"],
            Capabilities = PcCaps() with { Gpus = [PcCaps().Gpus[0]], Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS") },
        }, IPAddress.Parse(AgentIp));
        Assert.Equal(EnrollmentStatus.Enrolled, result.Status);
        return Spki.Sha256(key);
    }

    private static NodeRecord Approved(string id, string? pin = null) => new()
    {
        Id = id,
        Uid = id + "-uid",
        Address = $"https://{id}.lan:9443",
        Approved = true,
        CertSpkiSha256 = pin ?? Pin(),
        MemoryReserveGiB = 4,
    };

    private static string Pin()
    {
        using ECDsa key = PkiDefaults.CreateKey();
        return Spki.Sha256(key);
    }

    private static ModelDefinition Placed(string id, string node) =>
        new() { Id = id, ServedName = id, ModelPath = id, MemoryBudgetGiB = 20, Placement = new ModelPlacement(node) };

    private static NodeCapabilities PcCaps() => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "1.1.0",
        Os = new NodeOsInfo("windows", "Windows 11 Pro"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
        Gpus =
        [
            new GpuCapability { Index = 0, Uuid = "GPU-a", Name = "NVIDIA GeForce RTX 4060 Ti", MemoryTotalGiB = 16 },
            new GpuCapability { Index = 1, Uuid = "GPU-b", Name = "NVIDIA GeForce RTX 4060 Ti", MemoryTotalGiB = 16 },
        ],
    };

    private NodeLiveStatus Live(string id, NodeState state) => new()
    {
        NodeId = id,
        NodeUid = id + "-uid",
        Address = $"https://{id}.lan:9443",
        State = state,
        LastContact = _time.GetUtcNow().AddSeconds(-3),
        Capabilities = PcCaps(),
    };

    private NodeLiveStatus Dgx() => new()
    {
        NodeId = "dgx",
        NodeUid = "dgx-uid",
        DisplayName = "DGX Spark",
        Address = "https://127.0.0.1:9443",
        State = NodeState.Online,
        LastContact = _time.GetUtcNow().AddSeconds(-2),
        AgentVersion = "1.0.0",
        AgentProtocol = new ProtocolRange(1, 1),
        SelectedProtocol = 1,
        Capabilities = new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "1.0.0",
            Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS", "6.11.0-nvidia"),
            Arch = "arm64",
            Cpu = new NodeCpuInfo(20, "Cortex-X925"),
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 119.7),
            Gpus = [new GpuCapability { Index = 0, Uuid = "GPU-gb10", Name = "NVIDIA GB10", SharesHostMemory = true, ComputeCapability = "12.1" }],
            Runtimes = [new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = true }],
        },
        Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 119.7, HostAvailableGiB = 70 },
        Runtimes = [new NodeModelRuntime { ModelId = "qwen", NodeId = "dgx", State = ModelState.Ready }],
        CertNotAfter = _time.GetUtcNow().AddDays(80),
    };

    private static string Text<T>(IRenderedComponent<T> cut, string testId)
        where T : IComponent => cut.Find($"[data-testid={testId}]").TextContent.Trim();

    private static void InputAt<T>(IRenderedComponent<T> cut, string selector, string value)
        where T : IComponent =>
        cut.InvokeAsync(() => cut.Find(selector).InputAsync(new ChangeEventArgs { Value = value })).GetAwaiter().GetResult();

    private static void Submit<T>(IRenderedComponent<T> cut)
        where T : IComponent =>
        cut.InvokeAsync(() => cut.Find("[data-testid=node-settings-form]").SubmitAsync()).GetAwaiter().GetResult();

    private sealed class FakeCache : INodeStatusCache
    {
        private readonly Lock _gate = new();
        private List<NodeLiveStatus> _nodes = [];
        private long _version;

        public long Version => Interlocked.Read(ref _version);

        public void Set(params NodeLiveStatus[] nodes)
        {
            lock (_gate)
            {
                _nodes = [.. nodes];
            }

            Interlocked.Increment(ref _version);
        }

        public IReadOnlyList<NodeLiveStatus> ListNodes()
        {
            lock (_gate)
            {
                return [.. _nodes];
            }
        }

        public NodeLiveStatus? GetNode(string nodeId) => ListNodes().FirstOrDefault(n => n.NodeId == nodeId);

        public NodeModelRuntime? GetRuntime(string modelId) => ListRuntimes().FirstOrDefault(r => r.ModelId == modelId);

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) =>
            [.. ListNodes().Where(n => nodeId is null || n.NodeId == nodeId).SelectMany(n => n.Runtimes)];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeFencing(params NodeFencingStatus[] nodes) : IFencingCoordinator
    {
        public long Version => 1;

        public FencingStatus GetStatus() => new() { InstanceId = "master-1", Epoch = 5, Nodes = nodes };

        public Task<TakeoverRecord> TakeOverAsync(string actor, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TakeoverRecord> ContinueAsync(string nodeId, string takeoverId, long agentEpoch, string actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

/// <summary>The real registry, whose <c>ListAsync</c> can be held until released (an Add node dialog's read in flight).</summary>
internal sealed class GatedNodeRegistry(JsonNodeRegistry inner) : INodeRegistry
{
    private volatile TaskCompletionSource? _gate;

    public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => Interlocked.Exchange(ref _gate, null)?.TrySetResult();

    public async Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default)
    {
        if (_gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return await inner.ListAsync(ct);
    }

    public Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);

    public Task UpsertAsync(NodeRecord node, CancellationToken ct = default) => inner.UpsertAsync(node, ct);

    public Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default) => inner.UpdateAsync(id, update, ct);

    public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => inner.RevokeAsync(id, ct);

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);

    public Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default) =>
        inner.CreateJoinTokenAsync(nodeId, lifetime, purpose, ct);

    public Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default) =>
        inner.TryConsumeJoinTokenAsync(token, nodeId, ct);

    public Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default) => inner.ListJoinTokensAsync(ct);

    public Task<int> ExpireUnusedJoinTokensAsync(string nodeId, CancellationToken ct = default) => inner.ExpireUnusedJoinTokensAsync(nodeId, ct);

    public Task<int> PruneJoinTokensAsync(TimeSpan olderThan, CancellationToken ct = default) => inner.PruneJoinTokensAsync(olderThan, ct);
}
