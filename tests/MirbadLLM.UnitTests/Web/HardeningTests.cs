using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Auth;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Ui;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// The hardening pass: skip link, keyboard shortcuts and their help overlay, the connection-lost banner and
/// its non-dismissible dialog, container-query tables, the chat live region, radiogroups, aria-describedby
/// wiring, column help, and tidy dialog titles. Markup is checked as served by the real host; browser
/// behaviour that lives in js/shell.js is checked through the script's own contract.
/// </summary>
public sealed partial class HardeningTests
{
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

    private static async Task<string> GetHtmlAsync(string path, string? adminPassword = null)
    {
        using var factory = new AuthWebFactory { AdminPassword = adminPassword };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await client.GetStringAsync(path, Xunit.TestContext.Current.CancellationToken);
    }

    // ---------- Shell: skip link, shortcuts, reconnect ----------

    [Fact]
    public async Task SkipLink_IsWiredForScriptedFocus_OnAFocusableMain()
    {
        string html = await GetHtmlAsync("/");

        Assert.Matches(SkipLink(), html);
        Assert.Matches(MainTarget(), html);

        string shell = RepoFile("src/MirbadLLM.Web/wwwroot/js/shell.js");
        Assert.Contains("closest('[data-skip-link]')", shell, StringComparison.Ordinal);
        Assert.Contains("main.focus()", shell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortcutsHelp_IsAnAccessibleDialog_ListingEveryShortcut()
    {
        string html = await GetHtmlAsync("/");

        Assert.Matches(ShortcutsDialog(), html);
        Assert.Contains("id=\"shortcuts-title\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Keyboard shortcuts\"", html, StringComparison.Ordinal);
        foreach (string destination in new[] { "Overview", "Models", "Nodes", "Playground", "Access (Connect and API keys)", "Services", "Backup &amp; restore" })
        {
            Assert.Contains($"<dd>{destination}</dd>", html, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("<dd>Monitoring</dd>", html, StringComparison.Ordinal); // merged into the Overview

        Assert.Contains("<kbd>/</kbd>", html, StringComparison.Ordinal);
        Assert.Contains("<kbd>b</kbd>", html, StringComparison.Ordinal);
        Assert.Contains("<kbd>t</kbd>", html, StringComparison.Ordinal);
        Assert.Contains("<kbd>?</kbd>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Shortcuts_AreIgnoredWhileTyping_AndCoverEveryDestination()
    {
        string shell = RepoFile("src/MirbadLLM.Web/wwwroot/js/shell.js");

        // Typing in a text field, a select or a contenteditable never triggers a shortcut.
        Assert.Contains("if (isTyping(event.target) || isOpen())", shell, StringComparison.Ordinal);
        Assert.Contains("tag === 'TEXTAREA' || tag === 'SELECT'", shell, StringComparison.Ordinal);
        Assert.Contains("el.isContentEditable", shell, StringComparison.Ordinal);
        Assert.Contains("var GO = { d: '', m: 'models', s: 'services', c: 'chat', n: 'nodes', k: 'access', b: 'backup' };", shell, StringComparison.Ordinal);
        Assert.Contains("[data-shortcut-filter]", shell, StringComparison.Ordinal);
        Assert.Contains("modelShortcut(key === 'b' ? 'benchmark' : 'tune')", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void TabsKeyboardFocus_MovesInTheBrowser_BeforeTheServerSyncsSelection()
    {
        string shell = RepoFile("src/MirbadLLM.Web/wwwroot/js/shell.js");

        int focus = shell.IndexOf("next.focus();", StringComparison.Ordinal);
        int click = shell.IndexOf("next.click();", StringComparison.Ordinal);
        Assert.True(focus > 0 && click > focus, "focus must move before the click is sent to the server");
        Assert.Contains("closest('[data-roving-tabs]')", shell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconnect_ShowsANonBlockingBannerFirst_AndTheDialogCannotBeEscapedWhileDown()
    {
        string html = await GetHtmlAsync("/");

        Assert.Matches(ConnectionBanner(), html);
        Assert.Contains("Connection lost — reconnecting…", html, StringComparison.Ordinal);
        Assert.Contains("data-conn-age", html, StringComparison.Ordinal);
        Assert.Contains("id=\"components-reconnect-modal\"", html, StringComparison.Ordinal);

        // The client gives up on a silent server after 4 s (the server pings every second), not SignalR's 30 s.
        Assert.Contains("withServerTimeout(4000)", html, StringComparison.Ordinal);
        Assert.Contains("autostart=\"false\"", html, StringComparison.Ordinal);

        string js = RepoFile("src/MirbadLLM.Web/Components/Layout/ReconnectModal.razor.js");
        Assert.Contains("addEventListener(\"cancel\"", js, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault()", js, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"offline\"", js, StringComparison.Ordinal);
        // First attempt: banner only; the blocking dialog comes with "retrying".
        Assert.Matches(ShowIsBannerOnly(), js);
    }

    [Fact]
    public void Tables_StackByContainerWidth_AndScrollWithAVisibleAffordance()
    {
        string css = RepoFile("src/MirbadLLM.Web/wwwroot/app.css");

        Assert.Contains("container-type: inline-size;", css, StringComparison.Ordinal);
        Assert.Contains("@container (max-width: 759.98px) {\n    .data-table--stack thead { display: none; }", css.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("@container (max-width: 959.98px) {\n    .data-table--stack-wide thead { display: none; }", css.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("@media (max-width: 767.98px) {\n    .data-table--stack", css.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("scrollbar-width: thin;", css, StringComparison.Ordinal);
        Assert.DoesNotContain("--ss-border-strong", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrollingTablePanels_NeverShowASpuriousVerticalScrollbar()
    {
        // overflow-x: auto alone makes overflow-y compute to auto, so a sub-pixel of vertical overflow drew a
        // vertical scrollbar on every table panel; overflow-y must be pinned to hidden in the same rule.
        string css = RepoFile("src/MirbadLLM.Web/wwwroot/app.css").ReplaceLineEndings("\n");
        var rule = Regex.Matches(css, @"\n\.panel--scroll-x \{(?<body>[^}]*)\}")
            .Single(m => m.Groups["body"].Value.Contains("overflow-x", StringComparison.Ordinal));
        Assert.Contains("overflow-x: auto;", rule.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("overflow-y: hidden;", rule.Groups["body"].Value, StringComparison.Ordinal);

        // Anything that pops out of a table opens in the top layer instead of being clipped by that panel.
        Assert.Contains(".row-menu-list {", css, StringComparison.Ordinal);
        Assert.Matches(@"@supports \(position-anchor: --a\) \{\s*\.row-menu-list \{\s*position: fixed;", css);
    }

    [Fact]
    public void RowMenus_AreDrivenInTheBrowser_WithMenuKeyboardAndFocusReturn()
    {
        string shell = RepoFile("src/MirbadLLM.Web/wwwroot/js/shell.js");

        Assert.Contains("[data-row-menu-trigger]", shell, StringComparison.Ordinal);
        Assert.Contains("trigger.setAttribute('aria-expanded', open ? 'true' : 'false');", shell, StringComparison.Ordinal);
        foreach (string key in new[] { "'ArrowDown'", "'ArrowUp'", "'Home'", "'End'", "'Escape'", "'Tab'" })
        {
            Assert.Contains($"case {key}:", shell, StringComparison.Ordinal);
        }

        Assert.Contains("menu.hidePopover();", shell, StringComparison.Ordinal);
        Assert.Contains("back.focus();", shell, StringComparison.Ordinal);
        Assert.Contains("targetMenu.showPopover();", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void DarkAccentAndHover_KeepTheirButtonTextAtLeast4_5To1()
    {
        string css = RepoFile("src/MirbadLLM.Web/wwwroot/app.css");
        var dark = Regex.Match(css, @"\[data-bs-theme=dark\] \{(?<body>[^}]*)\}");
        string Token(string name)
        {
            var m = Regex.Match(dark.Groups["body"].Value, "--ss-" + name + @": (#[0-9a-f]{6})");
            Assert.True(m.Success, name);
            return m.Groups[1].Value;
        }

        string onAccent = Token("text-on-accent");
        foreach (string fill in new[] { Token("accent"), Token("accent-hover") })
        {
            Assert.True(Contrast(fill, onAccent) >= 4.5, $"{onAccent} text on {fill}");
        }
    }

    [Fact]
    public void LightAccent_KeepsButtonTextLinksAndFocusReadable()
    {
        // Amber is the brand's action colour: graphite ink on it, never white, and too light for text on paper,
        // so links use accent-text and the focus ring its own deeper amber.
        string css = RepoFile("src/MirbadLLM.Web/wwwroot/app.css");
        var light = Regex.Match(css, @":root,\s*\[data-bs-theme=light\] \{(?<body>[^}]*)\}");
        Assert.True(light.Success, "light token block");
        string Token(string name)
        {
            var m = Regex.Match(light.Groups["body"].Value, "--ss-" + name + @": (#[0-9a-f]{6})");
            Assert.True(m.Success, name);
            return m.Groups[1].Value;
        }

        foreach (string fill in new[] { Token("accent"), Token("accent-hover") })
        {
            Assert.True(Contrast(fill, Token("text-on-accent")) >= 4.5, $"button text on {fill}");
        }

        foreach (string ground in new[] { Token("page"), Token("panel"), Token("accent-subtle") })
        {
            Assert.True(Contrast(Token("accent-text"), ground) >= 4.5, $"accent text on {ground}");
        }

        Assert.True(Contrast(Token("focus"), Token("page")) >= 3, "focus ring on the page");
    }

    private static double Contrast(string a, string b)
    {
        static double Channel(int v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(string hex) =>
            0.2126 * Channel(Convert.ToInt32(hex.Substring(1, 2), 16))
            + 0.7152 * Channel(Convert.ToInt32(hex.Substring(3, 2), 16))
            + 0.0722 * Channel(Convert.ToInt32(hex.Substring(5, 2), 16));

        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // ---------- Login ----------

    [Fact]
    public async Task Login_Error_MarksThePasswordInvalid_AndDescribesIt()
    {
        string html = await GetHtmlAsync("/login?error=1", adminPassword: "secret");

        Assert.Matches(InvalidPassword(), html);
        Assert.Contains("id=\"login-error\"", html, StringComparison.Ordinal);
    }

    // ---------- ConfirmDialog ----------

    [Theory]
    [InlineData("Stop  ComfyUI ?", "Stop ComfyUI?")]
    [InlineData("Revoke ?", "Revoke?")]
    [InlineData("  Manage   grafana?  ", "Manage grafana?")]
    [InlineData("Start 2 services?", "Start 2 services?")]
    public void DialogTitles_HaveNoDoubleSpaces_OrSpaceBeforePunctuation(string raw, string expected) =>
        Assert.Equal(expected, ConfirmDialog.TidyTitle(raw));

    // ---------- Column help ----------

    [Fact]
    public void BenchmarkResult_ColumnsExplainThemselves_AndALegendFollows()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var run = new RunMeasurement(1, 256, 4.2, 60, 60, 0.1, 0.2, 0, 1);
        var level = new LevelResult(1, [run], 60, 60, 0.1, 0.2, false);
        var result = new BenchmarkResult([level], null, new GuardrailResult(true, "ok", 8, 8, []), NoiseReport.Quiet);

        var cut = ctx.Render<BenchmarkResultView>(p => p.Add(x => x.Result, result).Add(x => x.TestId, "r"));

        var button = cut.Find("[data-testid=colhelp-r-ttft95]");
        Assert.Equal("About: TTFT p95", button.GetAttribute("aria-label"));
        Assert.Equal(button.GetAttribute("popovertarget"), cut.Find("[data-testid=colhelp-panel-r-ttft95]").GetAttribute("id"));
        Assert.Contains("slowest 5 %", cut.Find("[data-testid=colhelp-panel-r-ttft95]").TextContent, StringComparison.Ordinal);
        Assert.Contains("TTFT = time to first token", cut.Find("[data-testid=r-legend]").TextContent, StringComparison.Ordinal);
    }

    [GeneratedRegex("<a class=\"skip-link\" href=\"#main\" data-skip-link")]
    private static partial Regex SkipLink();

    [GeneratedRegex("<main class=\"main\" id=\"main\" tabindex=\"-1\"")]
    private static partial Regex MainTarget();

    [GeneratedRegex("<dialog id=\"shortcuts-dialog\" class=\"ss-dialog shortcuts-dialog\" aria-labelledby=\"shortcuts-title\"")]
    private static partial Regex ShortcutsDialog();

    [GeneratedRegex("<div id=\"connection-banner\" class=\"conn-banner\" role=\"status\" aria-live=\"polite\" hidden")]
    private static partial Regex ConnectionBanner();

    [GeneratedRegex("state === \"show\"\\) \\{\\s*// First attempt: banner only[^}]*syncBanner\\(\\);\\s*\\}")]
    private static partial Regex ShowIsBannerOnly();

    [GeneratedRegex("<input type=\"password\"[^>]*aria-invalid=\"true\"[^>]*aria-describedby=\"login-error\"")]
    private static partial Regex InvalidPassword();
}
