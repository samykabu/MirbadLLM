using System.Reflection;
using System.Security.Cryptography;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Api;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Ui;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707, CA2000, xUnit1051 // underscore test names; the context owns the services it is given; the real services take no test token.

/// <summary>
/// MN-227 (design-p3p4 §A.7): the Nodes and Services pages for Windows nodes. The Nodes list carries an OS badge in a
/// mixed fleet (a Linux-only fleet renders as before); the join and rekey commands get the Windows tab with the token
/// prompt variant (<see cref="WindowsInstallerLine.Prompt"/>) beside the one-line form, the rekey dialog of a Windows
/// node opening on it; the node page lists the installed llama.cpp versions, the agent's clock skew, help for the new
/// Degraded reasons, a paused native download that needs the Hugging Face token again, and "measured at start" for a
/// process model (never "measured: 0"); the Services page shows Windows services with <c>services.scm</c> and keeps the
/// "does not manage services" text without it. The rekey and Add-node cases run over the real
/// <see cref="NodeAdminService"/> with a real <c>win-x64.zip</c> bundle.
/// </summary>
public sealed class NodesWindowsTests : IAsyncDisposable
{
    private const string Password = "correct horse battery staple";
    private const string Win = "win-1";
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-abcde";
    private const string WinSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string LinuxSha = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-mn227-" + Guid.NewGuid().ToString("N"));
    private readonly BunitContext _ctx = new();
    private JsonNodeRegistry? _nodes;
    private JsonModelRegistry? _models;
    private JsonlAuditLog? _audit;

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

    // ================================================================ Nodes list: OS badge

    [Fact]
    public void List_LinuxOnlyFleet_HasOsBadgesOnTheCards()
    {
        var cache = FakeCache.With(Dgx(), Live("pc-linux", LinuxPcCaps()));
        Register(cache, new NoRecords());

        var cut = _ctx.Render<Nodes>();

        Assert.Equal(2, cut.FindAll("[data-testid^=nodes-os-badge-]").Count);
        // OS · arch is a node fact: it lives on the node's page (Settings), not in a list column.
        Assert.Empty(cut.FindAll("[data-testid=nodes-os-dgx]"));
    }

    [Fact]
    public void List_MixedFleet_BadgesEveryNodeWithItsOs_PairedWithText()
    {
        var cache = FakeCache.With(Dgx(), Live(Win, WindowsCaps()));
        Register(cache, new NoRecords());

        var cut = _ctx.Render<Nodes>();

        Assert.Equal("Linux", Text(cut, "nodes-os-badge-dgx"));
        Assert.Equal("Windows", Text(cut, $"nodes-os-badge-{Win}"));
        Assert.Equal("windows", cut.Find($"[data-testid=nodes-os-badge-{Win}]").GetAttribute("data-os"));
        // A label, not a state: the neutral tag chip, inside the node's identity cell.
        Assert.Contains("tag-chip", cut.Find($"[data-testid=nodes-os-badge-{Win}]").ClassList);
        Assert.NotNull(cut.Find($"[data-testid=nodes-row-{Win}] .col-primary [data-testid=nodes-os-badge-{Win}]"));
    }

    [Fact]
    public void MixedOs_CountsOnlyKnownFamilies()
    {
        Assert.False(Nodes.MixedOs([Row("a", LinuxPcCaps()), Row("b", null)]));
        Assert.False(Nodes.MixedOs([Row("a", WindowsCaps()), Row("b", WindowsCaps())]));
        Assert.True(Nodes.MixedOs([Row("a", LinuxPcCaps()), Row("b", WindowsCaps())]));
        Assert.Equal("macOS", Nodes.OsBadgeText(LinuxPcCaps() with { Os = new NodeOsInfo("macos", "macOS 15") }));
        Assert.Null(Nodes.OsBadgeText(LinuxPcCaps() with { Os = new NodeOsInfo("plan9", "Plan 9") }));
    }

    // ================================================================ JoinCommandView: the Windows tab

    [Fact]
    public void JoinCommand_WindowsTab_DefaultsToThePrompt_WithTheTokenBeside_AndCopiesTheExactText()
    {
        var module = SetupCopy();
        JoinTokenCreateResponse response = JoinResponse();
        string prompt = WindowsInstallerLine.Join(Win, response.EnrollUrl, response.CaHash, response.AgentDownload)!.Prompt();

        var cut = _ctx.Render<JoinCommandView>(p => p.Add(x => x.Response, response).Add(x => x.Now, Now).Add(x => x.TestId, "jc"));

        // Linux first, as before; the Windows tab is offered.
        Assert.NotNull(cut.Find("[data-testid=jc-linux-prompt]"));
        Assert.NotNull(cut.Find("[data-testid=jc-os-tab-windows]"));
        Assert.Contains("A macOS agent is not available yet.", Text(cut, "jc-os-note"), StringComparison.Ordinal);

        cut.ClickAt("[data-testid=jc-os-tab-windows]");

        Assert.True(cut.Find("[data-testid=jc-windows-mode-prompt]").HasAttribute("checked"));
        Assert.Equal(prompt, Text(cut, "jc-windows-prompt"));
        Assert.DoesNotContain(Token, Text(cut, "jc-windows-prompt"), StringComparison.Ordinal);
        Assert.Contains(" -PromptToken; ", Text(cut, "jc-windows-prompt"), StringComparison.Ordinal);
        Assert.Contains("install -NodeId 'win-1'", Text(cut, "jc-windows-prompt"), StringComparison.Ordinal);
        Assert.Equal(Token, Text(cut, "jc-windows-token"));
        Assert.Contains("run as administrator", Text(cut, "jc-windows-prompt-note"), StringComparison.Ordinal);
        Assert.Contains("the safer line", Text(cut, "jc-windows-prompt-note"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=jc-windows]"));

        cut.ClickAt("[data-testid=jc-windows-prompt-copy]");
        cut.ClickAt("[data-testid=jc-windows-token-copy]");

        cut.ChangeAt("[data-testid=jc-windows-mode-inline]", true);
        Assert.Equal(response.Commands.Windows, Text(cut, "jc-windows"));
        Assert.StartsWith("$env:MIRBADLLM_JOIN_TOKEN='" + Token + "'; ", Text(cut, "jc-windows"), StringComparison.Ordinal);
        // MN-230: the note says what PSReadLine really does (out of the history file, still in this window and in a
        // transcript), and the prompt variant above says it is the safer line.
        string inlineNote = Text(cut, "jc-windows-inline-note");
        Assert.Contains("out of the history file", inlineNote, StringComparison.Ordinal);
        Assert.Contains("Up-arrow history and scrollback", inlineNote, StringComparison.Ordinal);
        Assert.Contains("Start-Transcript", inlineNote, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=jc-windows-prompt]"));
        Assert.Empty(cut.FindAll("[data-testid=jc-windows-token]"));
        cut.ClickAt("[data-testid=jc-windows-copy]");

        Assert.Equal([prompt, Token, response.Commands.Windows], module.Invocations["copyText"].Select(i => (string)i.Arguments[0]!).ToArray());

        // Back on Linux, its own token mode is untouched (still the prompt).
        cut.ClickAt("[data-testid=jc-os-tab-linux]");
        Assert.NotNull(cut.Find("[data-testid=jc-linux-prompt]"));
    }

    [Fact]
    public void JoinCommand_Rekey_OpensOnWindowsWhenAsked_WithTheRekeyPromptLine()
    {
        SetupCopy();
        JoinTokenCreateResponse response = RekeyResponse("win-1-uid");
        string prompt = WindowsInstallerLine.Rekey(Win, "win-1-uid", response.EnrollUrl, response.CaHash, response.AgentDownload)!.Prompt();

        var cut = _ctx.Render<JoinCommandView>(p => p.Add(x => x.Response, response).Add(x => x.NodeUid, "win-1-uid")
            .Add(x => x.Now, Now).Add(x => x.InitialOs, "windows").Add(x => x.TestId, "rk"));

        Assert.Equal("true", cut.Find("[data-testid=rk-os-tab-windows]").GetAttribute("aria-selected"));
        Assert.Equal(prompt, Text(cut, "rk-windows-prompt"));
        Assert.Contains("rekey -NodeId 'win-1' -NodeUid 'win-1-uid'", prompt, StringComparison.Ordinal);
        Assert.Contains("re-pairs it", Text(cut, "rk-windows-prompt-note"), StringComparison.Ordinal);
        Assert.Equal("Rekey token", cut.Find("[data-testid=rk-windows-token]").ParentElement!.ParentElement!.QuerySelector(".field-label")!.TextContent);
        cut.ChangeAt("[data-testid=rk-windows-mode-inline]", true);
        Assert.Equal(response.Commands.Windows, Text(cut, "rk-windows"));
    }

    [Fact]
    public void JoinCommand_AWindowsLineThePageCannotRebuild_ShowsOnlyTheMastersLine()
    {
        SetupCopy();
        JoinTokenCreateResponse good = JoinResponse();
        var response = good with { Commands = good.Commands with { Windows = good.Commands.Windows + " # another master's shape" } };

        var cut = _ctx.Render<JoinCommandView>(p => p.Add(x => x.Response, response).Add(x => x.Now, Now)
            .Add(x => x.InitialOs, "windows").Add(x => x.TestId, "jc"));

        Assert.Equal(response.Commands.Windows, Text(cut, "jc-windows"));
        Assert.Empty(cut.FindAll("[data-testid=jc-windows-mode]"));
        Assert.Empty(cut.FindAll("[data-testid=jc-windows-prompt]"));
        Assert.Null(JoinCommandView.WindowsPromptVariant(response, null));
    }

    [Fact]
    public void JoinCommand_InitialOs_FallsBackToLinux_WithoutACommandForIt()
    {
        JoinTokenCreateResponse linuxOnly = JoinResponse() with { Commands = new JoinCommands("sudo ./install-agent.sh") };
        Assert.Equal("linux", JoinCommandView.InitialTab("windows", linuxOnly));
        Assert.Equal("linux", JoinCommandView.InitialTab(null, JoinResponse()));
        Assert.Equal("windows", JoinCommandView.InitialTab("windows", JoinResponse()));
        Assert.Equal("linux", JoinCommandView.InitialTab("macos", JoinResponse()));
    }

    // ================================================================ The rekey and Add-node dialogs (real NodeAdminService)

    [Fact]
    public async Task RekeyDialog_OfAWindowsNode_OpensOnTheWindowsTab_WithItsPromptLine_ALinuxNodeOnLinux()
    {
        SetupAdmin();
        string winSha = WindowsBundleFixtures.Write(BundleDir);
        await Registry.UpsertAsync(Approved(Win) with { LastCapabilities = WindowsCaps() });
        await Registry.UpsertAsync(Approved("pc-linux") with { LastCapabilities = LinuxPcCaps() });
        _cache.Set(Live(Win, WindowsCaps()), Live("pc-linux", LinuxPcCaps()));
        var cut = RenderList();

        cut.ClickAt($"[data-testid=nodes-rekey-{Win}]");
        cut.ClickAt("[data-testid=node-rekey-confirm]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=node-rekey-command]")), Wait);

        Assert.Equal("true", cut.Find("[data-testid=node-rekey-command-os-tab-windows]").GetAttribute("aria-selected"));
        string prompt = Text(cut, "node-rekey-command-windows-prompt");
        string token = Text(cut, "node-rekey-command-windows-token");
        Assert.Contains($"rekey -NodeId 'win-1' -NodeUid 'win-1-uid' -EnrollUrl 'https://", prompt, StringComparison.Ordinal);
        Assert.Contains($"-ne '{winSha}'", prompt, StringComparison.Ordinal);
        Assert.EndsWith("-PromptToken; if ($LASTEXITCODE -ne 0) { throw \"install-agent.ps1 rekey failed (exit $LASTEXITCODE)\" } } finally { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; Remove-Item Env:MIRBADLLM_JOIN_TOKEN -ErrorAction SilentlyContinue } }", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(token, prompt, StringComparison.Ordinal);
        Assert.Contains("take", Text(cut, "node-rekey-windows-note"), StringComparison.Ordinal);
        cut.ChangeAt("[data-testid=node-rekey-command-windows-mode-inline]", true);
        Assert.StartsWith($"$env:MIRBADLLM_JOIN_TOKEN='{token}'; ", Text(cut, "node-rekey-command-windows"), StringComparison.Ordinal);
        cut.ClickAt("[data-testid=node-rekey-cancel]");

        // A Linux node's rekey still opens on Linux, with no Windows note.
        cut.ClickAt("[data-testid=nodes-rekey-pc-linux]");
        cut.ClickAt("[data-testid=node-rekey-confirm]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=node-rekey-command]")), Wait);
        Assert.Equal("true", cut.Find("[data-testid=node-rekey-command-os-tab-linux]").GetAttribute("aria-selected"));
        Assert.NotNull(cut.Find("[data-testid=node-rekey-command-linux-prompt]"));
        Assert.Empty(cut.FindAll("[data-testid=node-rekey-windows-note]"));
    }

    [Fact]
    public async Task AddNode_WithTheWindowsBundle_OffersTheWindowsTab_PromptLineInstallsWithLlamaCpp()
    {
        SetupAdmin();
        WindowsBundleFixtures.Write(BundleDir);
        var cut = RenderList();

        cut.ClickAt("[data-testid=nodes-add]");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=add-node-id-checking]")), Wait);
        InputAt(cut, "[data-testid=add-node-id]", Win);
        cut.ClickAt("[data-testid=add-node-confirm]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=add-node-command]")), Wait);

        cut.ClickAt("[data-testid=add-node-command-os-tab-windows]");
        string prompt = Text(cut, "add-node-command-windows-prompt");
        string token = Text(cut, "add-node-command-windows-token");
        Assert.Contains("install -NodeId 'win-1' -EnrollUrl 'https://", prompt, StringComparison.Ordinal);
        Assert.Contains(" -LlamaCpp -PromptToken; ", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(token, prompt, StringComparison.Ordinal);
        Assert.Contains("pinned llama.cpp", Text(cut, "add-node-command-windows-prompt-note"), StringComparison.Ordinal);
        Assert.Contains("A macOS agent is not available yet.", Text(cut, "add-node-command-os-note"), StringComparison.Ordinal);
        var token2 = (await Registry.ListJoinTokensAsync()).Single(t => t.NodeId == Win);
        Assert.Equal(JoinTokenPurpose.Join, token2.Purpose);
    }

    // ================================================================ Node detail

    [Fact]
    public void Detail_OfTheDgx_HasNoVersionsClockOrDownloads_AndTheAgentIsNotAsked()
    {
        var agents = new FakeAgents();
        var cache = FakeCache.With(Dgx(), Live(Win, WindowsCaps()));
        Register(cache, new NoRecords(), agents);

        var cut = RenderDetail("dgx");

        // Every node's header names its OS and runtimes now; the DGX's agent is still never asked for its own status.
        Assert.Equal("Linux", Text(cut, "node-os-badge"));
        Assert.Equal("vLLM", Text(cut, "node-runtime-chip-vllm"));
        Assert.Empty(cut.FindAll("[data-testid=node-reason-help]"));
        cut.ClickAt("[data-testid=node-tabs-tab-settings]");
        cut.ClickAt("[data-testid=node-settings-tabs-tab-hardware]");
        Assert.Empty(cut.FindAll("[data-testid^=node-runtime-versions-]"));
        Assert.Empty(cut.FindAll("[data-testid=node-clock]"));
        Assert.Equal("vLLM (container)", Text(cut, "node-runtimes"));
        cut.ClickAt("[data-testid=node-tabs-tab-operations]");
        Assert.Empty(cut.FindAll("[data-testid=node-downloads]"));
        Assert.Empty(agents.Calls);
    }

    [Theory]
    [InlineData("macos", "System Settings → General → Storage", "support.apple.com")]
    [InlineData("windows", "Settings → System → Storage → Cleanup recommendations", "support.microsoft.com")]
    [InlineData("linux", "df -h", null)]
    public void DegradedDiskWarningsHaveRecoveryStepsForTheNodesOs(string os, string instruction, string? guideHost)
    {
        var agents = new FakeAgents();
        var reasons = new[]
        {
            new HealthReason(HealthReasonCodes.ModelRootLow, "The models volume has 3.3% free (below 5%)."),
            new HealthReason(HealthReasonCodes.ModelRootLow, "The cache volume has 3.3% free (below 5%)."),
        };
        agents.Status["node"] = Snapshot(Now, reasons, []);
        var caps = Dgx().Capabilities! with { Os = new NodeOsInfo(os, os) };
        var cache = FakeCache.With(Live("node", caps, NodeState.Degraded, reasons.Select(r => r.Message).ToArray()));
        Register(cache, new NoRecords(), agents);

        var cut = RenderDetail("node");
        cut.WaitForAssertion(() => Assert.Contains(instruction, Text(cut, "node-disk-recovery"), StringComparison.Ordinal));
        Assert.Contains("Why this node is Degraded", Text(cut, "node-health-summary"), StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll("[data-testid=node-reasons] li").Count);
        Assert.Single(cut.FindAll("[data-testid=node-reason-help-model_root_low]"));
        Assert.Contains("threshold in the warning", Text(cut, "node-reason-help-model_root_low"), StringComparison.Ordinal);
        Assert.Contains("share a disk", Text(cut, "node-reason-help-model_root_low"), StringComparison.Ordinal);
        Assert.Contains("stopping a model does not free disk space", Text(cut, "node-disk-recovery"), StringComparison.Ordinal);
        Assert.Contains("all health warnings are resolved", Text(cut, "node-disk-recovery"), StringComparison.Ordinal);
        if (guideHost is null) Assert.Empty(cut.FindAll("[data-testid=node-disk-guide]"));
        else Assert.Equal(guideHost, new Uri(cut.Find("[data-testid=node-disk-guide]").GetAttribute("href")!).Host);
    }

    [Fact]
    public void HealthyContainerNodeDoesNotShowDiskRecoveryStepsOrReadExtraStatus()
    {
        var agents = new FakeAgents();
        Register(FakeCache.With(Dgx()), new NoRecords(), agents);
        var cut = RenderDetail("dgx");
        Assert.Empty(cut.FindAll("[data-testid=node-disk-recovery]"));
        Assert.Empty(cut.FindAll("[data-testid=node-health-summary]"));
        Assert.Empty(agents.Calls);
    }

    [Fact]
    public void Detail_OfAWindowsNode_ListsLlamaCppVersions_TheClock_ReasonHelp_AndAPausedDownloadNeedingTheToken()
    {
        var agents = new FakeAgents();
        agents.Status[Win] = Snapshot(Now.AddSeconds(45),
            [
                new HealthReason(HealthReasonCodes.GpuExternalUseHigh, "GPU 0: 6.0 GiB used by programs other than mirbadllm's models, above its 2.0 GiB reserve."),
                new HealthReason(HealthReasonCodes.ExecutableUnverified, "llama.cpp b11000 no longer matches its manifest."),
            ],
            [
                new NodeDownloadStatus { Folder = "qwen3-embedding-4b", RepoId = "Qwen/Qwen3-Embedding-4B-GGUF", State = DownloadState.Failed, NeedsToken = true, BytesDownloaded = 1_500_000_000, TotalBytes = 4_000_000_000, StartedAt = Now.AddMinutes(-9) },
                new NodeDownloadStatus { Folder = "other", RepoId = "org/other", State = DownloadState.Downloading, BytesDownloaded = 2_000_000, TotalBytes = 0, StartedAt = Now.AddMinutes(-1) },
            ]);
        var model = new ModelDefinition { Id = "qwen3-embedding-4b-win", ServedName = "qwen3-embedding", ModelPath = "Qwen3-Embedding-4B", MemoryBudgetGiB = 8, Runtime = RuntimeKind.LlamaCpp, Placement = new ModelPlacement(Win) };
        var cache = FakeCache.With(Dgx(), Live(Win, WindowsCaps(), NodeState.Degraded, ["GPU 0: 6.0 GiB used by programs other than mirbadllm's models."]));
        Register(cache, new NoRecords(), agents, [model]);

        var cut = RenderDetail(Win);

        Assert.Equal("Windows", Text(cut, "node-os-badge"));
        Assert.Equal("llama.cpp", Text(cut, "node-runtime-chip-llamacpp"));

        // Overview: what to do about each Degraded reason.
        Assert.Contains("raise the GPU's reserve under Settings", Text(cut, "node-reason-help-gpu_external_use_high"), StringComparison.Ordinal);
        Assert.Contains(@".\install-agent.ps1 -LlamaCpp", Text(cut, "node-reason-help-executable_unverified"), StringComparison.Ordinal);
        Assert.Contains("w32tm /query /status", Text(cut, "node-reason-help-clock_skew"), StringComparison.Ordinal);

        // Settings: hardware, agent, runtime versions and the clock.
        cut.ClickAt("[data-testid=node-tabs-tab-settings]");
        cut.ClickAt("[data-testid=node-settings-tabs-tab-hardware]");
        // MN-230: Razor trimmed the space before the VRAM, so the capability list read "RTX 5060 Ti16.0 GiB".
        Assert.Contains("RTX 5060 Ti 16.0 GiB", Normalize(cut.Find("[data-testid=node-capabilities]").TextContent), StringComparison.Ordinal);
        Assert.Contains("llama.cpp (process)", Text(cut, "node-runtimes"), StringComparison.Ordinal);
        Assert.Contains("b11476 · CUDA 13.4 · default", Text(cut, "node-runtime-version-llamacpp-process-b11476"), StringComparison.Ordinal);
        Assert.Equal("true", cut.Find("[data-testid=node-runtime-version-llamacpp-process-b11476]").GetAttribute("data-verified"));
        var unverified = cut.Find("[data-testid=node-runtime-version-llamacpp-process-b11000]");
        Assert.Equal("false", unverified.GetAttribute("data-verified"));
        Assert.Contains("Not verified", unverified.TextContent, StringComparison.Ordinal);
        Assert.NotNull(unverified.QuerySelector(".state--failed svg")); // colour paired with an icon and a label

        Assert.Equal("45 s ahead of the master (Degraded above 30 s; above 5 min the agent refuses changes)", Text(cut, "node-clock"));

        // Operations: the agent's downloads.
        cut.ClickAt("[data-testid=node-tabs-tab-operations]");
        var paused = cut.Find("[data-testid=node-download-needs-token-qwen3-embedding-4b]");
        Assert.Contains("needs the Hugging Face token again", paused.TextContent, StringComparison.Ordinal);
        Assert.Contains("qwen3-embedding's download row", Normalize(paused.TextContent), StringComparison.Ordinal);
        Assert.Equal("failed", cut.Find("[data-testid=node-download-qwen3-embedding-4b] .state").GetAttribute("data-state"));
        Assert.Contains("of", Text(cut, "node-download-qwen3-embedding-4b"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=node-download-needs-token-other]"));
        Assert.Equal([Win], agents.Calls.Distinct().ToArray());
    }

    [Fact]
    public void Detail_PausedDownloadOfAPlacedModel_LinksToItsEditor_CaseInsensitivelyOnWindows()
    {
        var agents = new FakeAgents();
        agents.Status[Win] = Snapshot(Now, [],
            [
                new NodeDownloadStatus { Folder = "qwen3-embedding-4b", RepoId = "Qwen/x", State = DownloadState.Failed, NeedsToken = true },
                new NodeDownloadStatus { Folder = "orphan", RepoId = "org/orphan", State = DownloadState.Failed, NeedsToken = true },
            ]);
        var model = new ModelDefinition { Id = "emb", ServedName = "qwen3-embedding", ModelPath = "Qwen3-Embedding-4B", MemoryBudgetGiB = 8, Placement = new ModelPlacement(Win) };
        var elsewhere = new ModelDefinition { Id = "elsewhere", ServedName = "elsewhere", ModelPath = "orphan", MemoryBudgetGiB = 8, Placement = new ModelPlacement("dgx") };
        Register(FakeCache.With(Live(Win, WindowsCaps())), new NoRecords(), agents, [model, elsewhere]);
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/nodes/{Win}?tab=operations");

        var cut = RenderDetail(Win);

        var link = cut.Find("[data-testid=node-download-needs-token-qwen3-embedding-4b] a");
        Assert.Equal("models/emb", link.GetAttribute("href"));
        Assert.Equal("qwen3-embedding", link.TextContent);
        // A folder of no model on this node: no link to another node's model, the editor in general.
        var orphan = cut.Find("[data-testid=node-download-needs-token-orphan]");
        Assert.Null(orphan.QuerySelector("a"));
        Assert.Contains("the model editor's download row", Normalize(orphan.TextContent), StringComparison.Ordinal);
        cut.ClickAt("[data-testid=node-tabs-tab-settings]");
        cut.ClickAt("[data-testid=node-settings-tabs-tab-hardware]");
        Assert.Equal("In step with the master (under 1 s apart)", Text(cut, "node-clock"));
        cut.ClickAt("[data-testid=node-tabs-tab-overview]");
        Assert.Empty(cut.FindAll("[data-testid=node-reason-help]"));
    }

    [Fact]
    public void Detail_DoesNotWaitForTheAgentStatusRead_AndFillsTheClockInWhenItLands()
    {
        // MN-239 item 6 (D131): the read is bounded at 5 s and runs on first load and on every 5 s tick, so an
        // unreachable Windows node must not hold the page back. Here the read is held open: the page is expected
        // anyway, with the agent's own part (the clock) arriving later.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agents = new FakeAgents { Hold = held };
        agents.Status[Win] = Snapshot(Now.AddSeconds(45), [], []);
        Register(FakeCache.With(Dgx(), Live(Win, WindowsCaps())), new NoRecords(), agents);
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/nodes/{Win}?tab=settings&section=hardware");

        var cut = _ctx.Render<NodeDetail>(p => p.Add(x => x.Id, Win));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=node-loading]")), Wait);
        Assert.Equal("Windows", Text(cut, "node-os-badge"));
        Assert.NotNull(cut.Find("[data-testid=node-runtime-versions-llamacpp-process]"));
        Assert.Empty(cut.FindAll("[data-testid=node-clock]")); // the agent has not answered yet
        Assert.Contains(Win, agents.Calls); // it was asked, the page just did not wait

        held.SetResult();

        cut.WaitForAssertion(
            () => Assert.Equal("45 s ahead of the master (Degraded above 30 s; above 5 min the agent refuses changes)", Text(cut, "node-clock")),
            Wait);
    }

    [Fact]
    public void Detail_AgentStatusUnreadable_SaysSo_AndTheRestOfThePageRenders()
    {
        var agents = new FakeAgents(); // no snapshot for win-1: the read throws
        Register(FakeCache.With(Live(Win, WindowsCaps())), new NoRecords(), agents);

        var cut = RenderDetail(Win);

        Assert.Contains("Could not read the agent's own status", Text(cut, "node-agent-details-error"), StringComparison.Ordinal);
        cut.ClickAt("[data-testid=node-tabs-tab-settings]");
        cut.ClickAt("[data-testid=node-settings-tabs-tab-hardware]");
        Assert.Empty(cut.FindAll("[data-testid=node-clock]"));
        Assert.NotNull(cut.Find("[data-testid=node-runtime-versions-llamacpp-process]"));
        cut.ClickAt("[data-testid=node-settings-tabs-tab-certificate]");
        Assert.NotNull(cut.Find("[data-testid=node-certs]"));
    }

    [Fact]
    public void Detail_ModelsTab_ShowsMeasuredAtStart_ForAProcessModel_NeverMeasuredZero()
    {
        var agents = new FakeAgents();
        agents.Status[Win] = Snapshot(Now, [], []);
        ModelDefinition Llama(string id) => new()
        {
            Id = id, ServedName = id, ModelPath = id, MemoryBudgetGiB = 8, Runtime = RuntimeKind.LlamaCpp, Placement = new ModelPlacement(Win),
            // A real llama.cpp model always carries its settings, and their delivery is what makes it a process instance.
            LlamaCpp = new LlamaCppSettings { ModelFile = $"{id}/{id}.gguf", Delivery = LaunchDelivery.Process },
        };
        var live = Live(Win, WindowsCaps()) with
        {
            Runtimes =
            [
                new NodeModelRuntime { ModelId = "measured", NodeId = Win, State = ModelState.Ready, InstanceId = "4242", MeasuredGpuGiB = 6.24 },
                new NodeModelRuntime { ModelId = "zero", NodeId = Win, State = ModelState.Ready, InstanceId = "4243", MeasuredGpuGiB = 0 },
                new NodeModelRuntime { ModelId = "starting", NodeId = Win, State = ModelState.Starting, InstanceId = "4244" },
            ],
        };
        var dgxModel = new ModelDefinition { Id = "qwen", ServedName = "qwen", ModelPath = "qwen", MemoryBudgetGiB = 40, Placement = new ModelPlacement("dgx") };
        var dgx = Dgx() with { Runtimes = [new NodeModelRuntime { ModelId = "qwen", NodeId = "dgx", State = ModelState.Ready, MeasuredGpuGiB = 37.5 }] };
        Register(FakeCache.With(dgx, live), new NoRecords(), agents, [Llama("measured"), Llama("zero"), Llama("starting"), dgxModel]);
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/nodes/{Win}?tab=models");

        var cut = RenderDetail(Win);

        Assert.Equal("measured at start 6.2 GiB", Normalize(Text(cut, "node-model-measured-measured")));
        Assert.Empty(cut.FindAll("[data-testid=node-model-measured-zero]"));
        Assert.Empty(cut.FindAll("[data-testid=node-model-measured-starting]"));
        Assert.DoesNotContain("measured: 0", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("measured at start 0", Normalize(cut.Markup), StringComparison.Ordinal);

        // A container model's measured value is never labelled "measured at start".
        var row = Nodes.Merge(null, dgx, [dgxModel]);
        Assert.Null(NodeDetail.MeasuredAtStart(row, dgxModel, dgx.Runtimes[0]));

        // MN-230: the model's own delivery decides. A node that reports a container llama.cpp runtime next to the
        // process one (the master supports both kinds) used to hide the figure for its process models.
        var bothKinds = live with
        {
            Capabilities = WindowsCaps() with
            {
                Runtimes =
                [
                    new RuntimeCapability { Kind = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Process, Available = true },
                    new RuntimeCapability { Kind = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Container, Available = true },
                ],
            },
        };
        var processModel = Llama("measured");
        var mixedRow = Nodes.Merge(null, bothKinds, [processModel]);
        Assert.Equal(6.24, NodeDetail.MeasuredAtStart(mixedRow, processModel, bothKinds.Runtimes[0]));
        Assert.Null(NodeDetail.MeasuredAtStart(mixedRow, processModel with { LlamaCpp = new LlamaCppSettings { ModelFile = "m/m.gguf", Delivery = LaunchDelivery.Container } }, bothKinds.Runtimes[0]));
    }

    [Fact]
    public void ClockAndReasonTexts()
    {
        Assert.Equal("In step with the master (under 1 s apart)", NodeDetail.ClockText(TimeSpan.FromMilliseconds(-900)));
        Assert.Equal("12 s behind the master", NodeDetail.ClockText(TimeSpan.FromSeconds(-12)));
        Assert.Equal("30 s ahead of the master", NodeDetail.ClockText(TimeSpan.FromSeconds(30)));
        Assert.Equal("2.5 min behind the master (Degraded above 30 s; above 5 min the agent refuses changes)", NodeDetail.ClockText(TimeSpan.FromSeconds(-150)));
        Assert.Contains("w32tm", NodeDetail.ReasonHelpText(HealthReasonCodes.ClockSkew, windows: true), StringComparison.Ordinal);
        Assert.DoesNotContain("w32tm", NodeDetail.ReasonHelpText(HealthReasonCodes.ClockSkew, windows: false), StringComparison.Ordinal);
        Assert.Null(NodeDetail.ReasonHelpText(HealthReasonCodes.DockerUnreachable, windows: true));
        Assert.True(NodeDetail.UsesAgentDetails(Row(Win, WindowsCaps())));
        Assert.False(NodeDetail.UsesAgentDetails(Row("dgx", Dgx().Capabilities)));
        Assert.True(NodeDetail.UsesAgentDetails(Row("pc-linux", LinuxPcCaps() with { Features = [AgentFeatures.DownloadsHfNative] })));
        Assert.False(NodeDetail.UsesAgentDetails(Row(Win, WindowsCaps()) with { State = NodeState.Offline }));
    }

    // ================================================================ Services

    [Fact]
    public void Services_WindowsNodeWithScm_ListsWindowsServices_WithoutAKindFilter_AndTheRightsNote()
    {
        var win = new ScmManager();
        RegisterServices(new Dictionary<string, IHostServiceManager> { [Win] = win }, WindowsNode(scm: true));
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/services?node={Win}");

        var cut = _ctx.Render<Services>();

        Assert.Empty(cut.FindAll("[data-testid=services-unsupported]"));
        Assert.NotNull(cut.Find("[data-testid=service-row-ollama]"));
        Assert.Empty(cut.FindAll("[data-testid=host-agent-missing]"));

        cut.ClickAt("[data-testid=services-tabs-tab-discovered]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=discovered-group-all]")), Wait);
        Assert.Contains("Every Windows service on Windows PC", Text(cut, "discovered-intro-scm"), StringComparison.Ordinal);
        // Windows has services only: no kind filter, and no Kind column.
        Assert.Empty(cut.FindAll("[data-testid=discovered-kind]"));
        Assert.DoesNotContain("Kind", cut.Find("[data-testid=discovered-group-all] thead").TextContent, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find("[data-testid=discovered-row-system-WinDefend]").GetAttribute("data-denied"));

        cut.ClickAt("[data-testid=add-system-Spooler]");
        // The name is a quoted PowerShell argument: real service names hold spaces and parentheses, and the operator
        // pastes this line into an administrator shell (MN-230).
        Assert.Contains(@".\install-agent.ps1 -AllowService 'Spooler'", Text(cut, "whitelist-scm-rights"), StringComparison.Ordinal);
    }

    [Fact]
    public void Services_WindowsNodeWhoseScmIsDown_SaysSo_WithoutTheHostdInstaller()
    {
        var win = new ScmManager { Available = false };
        RegisterServices(new Dictionary<string, IHostServiceManager> { [Win] = win }, WindowsNode(scm: true));
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/services?node={Win}");

        var cut = _ctx.Render<Services>();

        Assert.Contains("Windows services can't be read", Text(cut, "scm-unavailable"), StringComparison.Ordinal);
        Assert.Contains("Access is denied", Text(cut, "scm-unavailable"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=host-agent-missing]"));
        Assert.DoesNotContain("install-hostd.sh", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]  // reports an SCM manager, but not services.scm (the feature is off)
    [InlineData(false)] // reports no host services at all (an agent from before MN-228)
    public void Services_WindowsNodeWithoutServicesScm_KeepsTheDoesNotManageText(bool reportsManager)
    {
        var win = new ScmManager();
        RegisterServices(new Dictionary<string, IHostServiceManager> { [Win] = win }, WindowsNode(scm: false, reportsManager));
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/services?node={Win}");

        var cut = _ctx.Render<Services>();

        Assert.Contains("Windows PC can't manage services", Text(cut, "services-unsupported"), StringComparison.Ordinal);
        Assert.Contains("does not offer Windows service control", Text(cut, "services-unsupported-why"), StringComparison.Ordinal);
        Assert.Contains("install-agent.ps1", Text(cut, "services-unsupported-why"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=services-tabs]"));
        Assert.Equal(0, win.Lists);
    }

    [Fact]
    public void Services_LinuxNodeWithoutHostd_StillShowsTheHostdInstaller_AndEveryKind()
    {
        var pc = new ScmManager { Available = false };
        var linux = new NodeRecord
        {
            Id = "pc-linux",
            Uid = "pc-linux-uid",
            Address = "https://pc-linux.lan:9443",
            DisplayName = "Linux PC",
            Approved = true,
            LastCapabilities = LinuxPcCaps() with { HostServices = new HostServicesCapability("systemd", false, null, true), Features = [AgentFeatures.ServicesSystemd] },
        };
        RegisterServices(new Dictionary<string, IHostServiceManager> { ["pc-linux"] = pc }, linux);
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/services?node=pc-linux");

        var cut = _ctx.Render<Services>();

        Assert.Contains("install-hostd.sh", Text(cut, "host-agent-missing"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=scm-unavailable]"));
        cut.ClickAt("[data-testid=services-tabs-tab-discovered]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=discovered-kind]")), Wait);
        Assert.Equal(["all", "system", "containers", "user"], cut.FindAll("[data-testid=discovered-kind] input[type=radio]").Select(r => r.GetAttribute("value")).ToArray());
        Assert.Empty(cut.FindAll("[data-testid=discovered-intro-scm]"));
    }

    // ================================================================ Services on a Mac (Phase 4, M10)

    private static NodeRecord MacNode(HostServicesCapability? hostServices, params string[] features) => new()
    {
        Id = "mac-1",
        Uid = "mac-1-uid",
        Address = "https://mac-1.lan:9443",
        DisplayName = "Mac mini",
        Approved = true,
        LastCapabilities = LinuxPcCaps() with
        {
            Os = new NodeOsInfo("macos", "macOS 27.0.1"),
            Arch = "arm64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 8),
            Features = features,
            HostServices = hostServices,
        },
    };

    [Fact]
    public void Services_MacNodeWithHostd_ListsLaunchdJobs_WithoutAContainerKind()
    {
        RegisterServices(new Dictionary<string, IHostServiceManager> { ["mac-1"] = new ScmManager() },
            MacNode(new HostServicesCapability(HostServiceManagers.Launchd, true, null, false), AgentFeatures.ServicesLaunchd));
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/services?node=mac-1");

        var cut = _ctx.Render<Services>();

        Assert.Empty(cut.FindAll("[data-testid=services-unsupported]"));
        Assert.NotNull(cut.Find("[data-testid=service-row-ollama]"));
        cut.ClickAt("[data-testid=services-tabs-tab-discovered]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=discovered-kind]")), Wait);
        string intro = Text(cut, "discovered-intro-launchd");
        Assert.Contains("LaunchDaemon", intro, StringComparison.Ordinal);
        Assert.Contains("GitHub Actions runners", intro, StringComparison.Ordinal);
        Assert.Equal(["all", "system", "user"], cut.FindAll("[data-testid=discovered-kind] input[type=radio]").Select(r => r.GetAttribute("value")).ToArray());
        Assert.Empty(cut.FindAll("[data-testid=discovered-intro-scm]"));
    }

    [Fact]
    public void Services_MacNodeWhoseHostdIsDown_ShowsTheMacInstallCommand_NotTheLinuxOne()
    {
        RegisterServices(new Dictionary<string, IHostServiceManager> { ["mac-1"] = new ScmManager { Available = false } },
            MacNode(new HostServicesCapability(HostServiceManagers.Launchd, false, null, false), AgentFeatures.ServicesLaunchd));
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/services?node=mac-1");

        var cut = _ctx.Render<Services>();

        Assert.Equal("sudo bash install-agent.sh install-hostd", Text(cut, "host-agent-install-mac"));
        Assert.Contains("/var/run/mirbadllm-hostd/hostd.sock", Text(cut, "host-agent-missing-mac"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=host-agent-missing]"));
        Assert.DoesNotContain("install-hostd.sh", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Services_MacNodeWithoutHostd_SaysHowToInstallIt()
    {
        RegisterServices(new Dictionary<string, IHostServiceManager> { ["mac-1"] = new ScmManager() }, MacNode(null));
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/services?node=mac-1");

        var cut = _ctx.Render<Services>();

        Assert.Contains("install-agent.sh install-hostd", Text(cut, "services-unsupported-why"), StringComparison.Ordinal);
        Assert.Contains("launchd jobs", Text(cut, "services-unsupported-why"), StringComparison.Ordinal);
    }

    // ================================================================ Builders

    private string BundleDir => Path.Combine(_root, "bundles");

    private string DataDir => Path.Combine(_root, "data");

    private JsonNodeRegistry Registry => _nodes ??= new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = DataDir }), TimeProvider.System);

    private JsonModelRegistry Models => _models ??= new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = DataDir }));

    private JsonlAuditLog Audit => _audit ??= new JsonlAuditLog(Path.Combine(DataDir, "audit"), TimeProvider.System);

    private readonly FakeCache _cache = new();

    private static NodeCapabilities WindowsCaps() => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "1.2.0",
        Os = new NodeOsInfo("windows", "Windows 11 Pro", "10.0.26300"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
        Features = [AgentFeatures.RuntimeLlamaCppProcess, AgentFeatures.DownloadsHfNative],
        Gpus = [new GpuCapability { Index = 0, Uuid = "GPU-5060", Name = "NVIDIA GeForce RTX 5060 Ti", MemoryTotalGiB = 16 }],
        Runtimes =
        [
            new RuntimeCapability
            {
                Kind = RuntimeKind.LlamaCpp,
                Delivery = LaunchDelivery.Process,
                Available = true,
                Versions = [new RuntimeVersion("b11000", "13.4", Verified: false), new RuntimeVersion("b11476", "13.4", Verified: true, Default: true)],
            },
        ],
    };

    private static NodeCapabilities LinuxPcCaps() => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "1.1.0",
        Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 32),
    };

    private static NodeLiveStatus Dgx() => new()
    {
        NodeId = "dgx",
        NodeUid = "dgx-uid",
        DisplayName = "DGX Spark",
        State = NodeState.Online,
        LastContact = Now.AddSeconds(-2),
        Capabilities = new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "1.0.0",
            Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS", "6.11.0-nvidia"),
            Arch = "arm64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 119.7),
            Features = [AgentFeatures.DownloadsHfContainer, AgentFeatures.ServicesSystemd],
            Runtimes = [new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = true }],
        },
    };

    private static NodeLiveStatus Live(string id, NodeCapabilities caps, NodeState state = NodeState.Online, IReadOnlyList<string>? reasons = null) => new()
    {
        NodeId = id,
        NodeUid = id + "-uid",
        DisplayName = id == Win ? "Windows PC" : null,
        Address = $"https://{id}.lan:9443",
        State = state,
        Reasons = reasons ?? [],
        LastContact = Now.AddSeconds(-3),
        Capabilities = caps,
    };

    private static Nodes.NodeRow Row(string id, NodeCapabilities? caps) => new() { Id = id, Name = id, State = NodeState.Online, Capabilities = caps };

    private static NodeRecord Approved(string id) => new()
    {
        Id = id,
        Uid = id + "-uid",
        Address = $"https://{id}.lan:9443",
        Approved = true,
        CertSpkiSha256 = Pin(),
    };

    private static string Pin()
    {
        using ECDsa key = PkiDefaults.CreateKey();
        return Spki.Sha256(key);
    }

    private static NodeRecord WindowsNode(bool scm, bool reportsManager = true) => new()
    {
        Id = Win,
        Uid = Win + "-uid",
        Address = "https://win-1.lan:9443",
        DisplayName = "Windows PC",
        Approved = true,
        LastCapabilities = WindowsCaps() with
        {
            Features = scm ? [AgentFeatures.ServicesScm] : [],
            HostServices = reportsManager ? new HostServicesCapability(HostServiceManagers.Scm, true, null, false) : null,
        },
    };

    private static AgentDownload Download() => new()
    {
        Bundles =
        [
            new AgentBundleLink("linux-x64", "http://master:8080/agent/bundles/linux-x64.tar.gz", LinuxSha, "v1"),
            new AgentBundleLink("win-x64", "http://master:8080/agent/bundles/win-x64.zip", WinSha, "v1"),
        ],
    };

    private static JoinTokenCreateResponse JoinResponse()
    {
        const string enroll = "https://192.0.2.191:8443";
        const string ca = "sha256:ab:cd";
        AgentDownload download = Download();
        return new JoinTokenCreateResponse
        {
            NodeId = Win,
            Token = Token,
            CaHash = ca,
            EnrollUrl = enroll,
            ExpiresAt = Now.AddMinutes(60),
            AgentDownload = download,
            Commands = new JoinCommands(
                LinuxInstallerLine.Join(Win, enroll, ca, download).WithToken(Token),
                WindowsInstallerLine.Join(Win, enroll, ca, download)!.WithToken(Token)),
        };
    }

    private static JoinTokenCreateResponse RekeyResponse(string uid)
    {
        JoinTokenCreateResponse join = JoinResponse();
        return join with
        {
            Purpose = JoinTokenPurpose.Rekey,
            Commands = new JoinCommands(
                LinuxInstallerLine.Rekey(Win, uid, join.EnrollUrl, join.CaHash, join.AgentDownload).WithToken(Token),
                WindowsInstallerLine.Rekey(Win, uid, join.EnrollUrl, join.CaHash, join.AgentDownload)!.WithToken(Token)),
        };
    }

    private static StatusSnapshot Snapshot(DateTimeOffset agentTime, IReadOnlyList<HealthReason> reasons, IReadOnlyList<NodeDownloadStatus> downloads) => new()
    {
        BootId = "boot-1",
        Seq = 1,
        Time = agentTime,
        Health = new NodeHealth { Reasons = reasons },
        Memory = new NodeMemorySnapshot { Model = MemoryModel.Discrete, HostTotalGiB = 64, HostAvailableGiB = 40 },
        Downloads = downloads,
    };

    private BunitJSModuleInterop SetupCopy()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = _ctx.JSInterop.SetupModule("./interop.js");
        module.Setup<bool>("copyText", _ => true).SetResult(true);
        return module;
    }

    private void Register(FakeCache cache, INodeRegistry registry, FakeAgents? agents = null, IEnumerable<ModelDefinition>? models = null)
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton<INodeStatusCache>(cache);
        _ctx.Services.AddSingleton(registry);
        _ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry([.. models ?? []]));
        _ctx.Services.AddSingleton<TimeProvider>(new FixedTime(Now));
        if (agents is not null)
        {
            _ctx.Services.AddSingleton<IAgentClientFactory>(agents);
        }
    }

    private void RegisterServices(Dictionary<string, IHostServiceManager> perNode, params NodeRecord[] others)
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var dgx = new NodeRecord
        {
            Id = "dgx",
            Uid = "dgx-uid",
            Address = "https://127.0.0.1:9443",
            Approved = true,
            LastCapabilities = Dgx().Capabilities! with { HostServices = new HostServicesCapability("systemd", true, "1.0", true) },
        };
        _ctx.Services.AddSingleton<IHostServiceManager>(new ScmManager());
        _ctx.Services.AddSingleton<INodeHostServices>(new PerNodeServices(perNode));
        _ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(121, 50)));
        _ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry());
        _ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        _ctx.Services.AddSingleton<INodeRegistry>(new ListOnlyNodeRegistry([dgx, .. others]));
    }

    private void SetupAdmin()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetClaims(TestAdmin.User.Claims.ToArray());
        _ctx.JSInterop.SetupModule("./interop.js").Setup<bool>("copyText", _ => true).SetResult(true);
        var services = _ctx.Services;
        services.AddLogging();
        services.AddOptions();
        services.Configure<MirbadLLMOptions>(o => o.DataPath = DataDir);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INodeRegistry>(_ => Registry);
        services.AddSingleton<IModelRegistry>(_ => Models);
        services.AddSingleton<IAuditLog>(_ => Audit);
        services.AddSingleton(CertificateAuthority.Create());
        services.AddSingleton<INodeStatusCache>(_cache);
        services.AddSingleton<IMetricsHistory>(new FakeMetricsHistory());
        services.AddOptions<AuthOptions>().Configure(o => o.AdminPassword = Password);
        services.AddMirbadLLMAdminReauth();
        services.AddMirbadLLMNodeApi(new ConfigurationBuilder().Build());
        services.Configure<NodeApiOptions>(o => o.AgentBundleDirectory = BundleDir);
    }


    private IRenderedComponent<Nodes> RenderList()
    {
        var cut = _ctx.Render<Nodes>();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=nodes-loading]")), Wait);
        return cut;
    }

    private IRenderedComponent<NodeDetail> RenderDetail(string id)
    {
        var cut = _ctx.Render<NodeDetail>(p => p.Add(x => x.Id, id));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=node-loading]")), Wait);
        return cut;
    }

    private static string Text<T>(IRenderedComponent<T> cut, string testId)
        where T : IComponent => cut.Find($"[data-testid={testId}]").TextContent.Trim();

    private static string Normalize(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static void InputAt<T>(IRenderedComponent<T> cut, string selector, string value)
        where T : IComponent =>
        cut.InvokeAsync(() => cut.Find(selector).InputAsync(new ChangeEventArgs { Value = value })).GetAwaiter().GetResult();

    // ================================================================ Fakes

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeCache : INodeStatusCache
    {
        private readonly Lock _gate = new();
        private List<NodeLiveStatus> _nodes = [];
        private long _version;

        public static FakeCache With(params NodeLiveStatus[] nodes)
        {
            var cache = new FakeCache();
            cache.Set(nodes);
            return cache;
        }

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
            ListNodes().Where(n => nodeId is null || n.NodeId == nodeId).SelectMany(n => n.Runtimes).ToList();

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Agents by node id answering only <c>/status</c>; records which nodes were asked.</summary>
    private sealed class FakeAgents : IAgentClientFactory
    {
        public Dictionary<string, StatusSnapshot> Status { get; } = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        /// <summary>When set, every status read waits for it (MN-239 item 6): an unreachable node, held open.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public IAgentClient GetClient(string nodeId) => StatusOnlyAgent.Create(this, nodeId);

        public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

        public void RebuildHandlers(string nodeId) => throw new NotSupportedException();

        public void SetNegotiatedProtocol(string nodeId, int version) => throw new NotSupportedException();

        internal async Task<StatusSnapshot> StatusAsync(string nodeId)
        {
            lock (Calls)
            {
                Calls.Add(nodeId);
            }

            if (Hold is { } hold)
            {
                await hold.Task;
            }

            return Status.TryGetValue(nodeId, out StatusSnapshot? snapshot)
                ? snapshot
                : throw new AgentUnreachableException($"{nodeId}: connection refused");
        }
    }

    /// <summary>An <see cref="IAgentClient"/> answering <c>GetStatusAsync</c> only; every other call fails.</summary>
    public class StatusOnlyAgent : DispatchProxy
    {
        private object? _agents;
        private string _nodeId = "";

        internal static IAgentClient Create(object agents, string nodeId)
        {
            IAgentClient client = DispatchProxy.Create<IAgentClient, StatusOnlyAgent>();
            var proxy = (StatusOnlyAgent)(object)client;
            proxy._agents = agents;
            proxy._nodeId = nodeId;
            return client;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IAgentClient.GetStatusAsync) => ((FakeAgents)_agents!).StatusAsync(_nodeId),
            "get_NodeId" => _nodeId,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }

    /// <summary>A registry without records: the live cache alone gives the rows.</summary>
    private sealed class NoRecords : INodeRegistry
    {
        public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodeRecord>>([]);

        public Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<NodeRecord?>(null);

        public Task UpsertAsync(NodeRecord node, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JoinTokenRecord>>([]);
    }

    private sealed class PerNodeServices(Dictionary<string, IHostServiceManager> managers) : INodeHostServices
    {
        public Task<IHostServiceManager?> ForNodeAsync(string nodeId, CancellationToken ct = default) =>
            Task.FromResult(managers.GetValueOrDefault(nodeId));
    }

    /// <summary>Windows services as MN-228 maps them onto the page's rows (System kind, the service key as the unit).</summary>
    private sealed class ScmManager : IHostServiceManager
    {
        public bool Available { get; init; } = true;

        public int Lists { get; private set; }

        public Task<HostServicesSnapshot> ListAsync(CancellationToken ct = default)
        {
            Lists++;
            return Task.FromResult(Available
                ? new HostServicesSnapshot([Status("ollama", "Ollama")], true, null)
                : new HostServicesSnapshot([], false, "OpenSCManager: Access is denied."));
        }

        public Task<HostServicesDiscovery> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(new HostServicesDiscovery(
            [
                new DiscoveredService("ollama", "ollama", HostServiceKind.System, "Ollama", HostServiceState.Running, "running", "enabled", 2L << 30, null, true, false, null),
                new DiscoveredService("Spooler", "Spooler", HostServiceKind.System, "Print Spooler", HostServiceState.Running, "running", "enabled", 20L << 20, null, false, false, null),
                new DiscoveredService("WinDefend", "WinDefend", HostServiceKind.System, "Microsoft Defender Antivirus Service", HostServiceState.Running, "running", "enabled", 200L << 20, null, false, true, "WinDefend"),
            ], Available, Available ? null : "OpenSCManager: Access is denied.", []));

        public Task<HostServiceStatus> AddToWhitelistAsync(HostServiceWhitelistAdd request, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostServiceStatus> UpdateWhitelistAsync(string name, HostServiceWhitelistUpdate request, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostServiceStatus> RunAsync(string name, HostServiceAction action, string? confirm = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostServiceStatus> SetAutostartAsync(string name, bool enabled, string? confirm = null, CancellationToken ct = default) => throw new NotSupportedException();

        private static HostServiceStatus Status(string name, string display) =>
            new(name, display, "", HostServiceKind.System, name, false, null, HostServiceState.Running, "running", true, "enabled", Now, 2L << 30, null);
    }
}
