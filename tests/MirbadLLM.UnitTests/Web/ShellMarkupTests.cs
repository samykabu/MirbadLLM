using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using MirbadLLM.UnitTests.Auth;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// The app shell as actually served by the real host: the theme toggle is a plain button driven by
/// js/shell.js (the layout is statically rendered, so a Blazor @onclick there would never run), the theme is
/// set before first paint, assets are self-hosted, and the skip link and drawer toggle are wired up.
/// </summary>
public sealed partial class ShellMarkupTests
{
    private static async Task<string> GetDashboardHtmlAsync(CancellationToken ct)
    {
        using var factory = new AuthWebFactory { AdminPassword = null };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await client.GetStringAsync("/", ct);
    }

    [Fact]
    public async Task ThemeToggle_IsAPlainButtonWithPressedState_DrivenByShellScript()
    {
        string html = await GetDashboardHtmlAsync(TestContext.Current.CancellationToken);

        var toggle = ThemeToggleTag().Match(html);
        Assert.True(toggle.Success, "theme toggle button not found");
        Assert.Contains("type=\"button\"", toggle.Value, StringComparison.Ordinal);
        Assert.Contains("aria-pressed=", toggle.Value, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Dark theme\"", toggle.Value, StringComparison.Ordinal);
        Assert.Matches(ShellScript(), html);

        // Set before paint from storage (or the OS preference), in <head>.
        int head = html.IndexOf("</head>", StringComparison.Ordinal);
        int themeScript = html.IndexOf("localStorage.getItem('mirbadllm-theme')", StringComparison.Ordinal);
        Assert.InRange(themeScript, 0, head);
    }

    [Fact]
    public async Task Assets_AreSelfHosted()
    {
        string html = await GetDashboardHtmlAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("cdn.jsdelivr.net", html, StringComparison.Ordinal);
        Assert.Matches(BootstrapCss(), html);
    }

    [Fact]
    public async Task Shell_HasSkipLinkDrawerToggleAndStatusReadouts()
    {
        string html = await GetDashboardHtmlAsync(TestContext.Current.CancellationToken);

        Assert.Contains("href=\"#main\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"main\"", html, StringComparison.Ordinal);
        Assert.Matches(DrawerToggle(), html);
        Assert.Contains("data-testid=\"status-readouts\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Main\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rail_OrdersWatchRunFleetAdmin_AndKeepsFooterControls()
    {
        string html = await GetDashboardHtmlAsync(TestContext.Current.CancellationToken);
        using var doc = new HtmlParser().ParseDocument(html);
        var rail = doc.QuerySelector("#sidenav")!;
        Assert.Equal(["Run", "Fleet", "Admin"], rail.QuerySelectorAll(".nav-group-title").Select(x => x.TextContent));
        // Monitoring merged into the Overview: one Watch item.
        Assert.Equal(["", "models", "chat", "access", "nodes", "backup", "releases"],
            rail.QuerySelectorAll(".nav-group > .nav-list .nav-link-ss").Select(x => x.GetAttribute("href")));
        Assert.Equal(7, rail.QuerySelectorAll(".nav-group .nav-tile").Length);
        Assert.NotNull(rail.QuerySelector("[data-tour=nav] .active"));
        Assert.NotNull(rail.QuerySelector("[data-testid=controller-version-link] [data-testid=controller-version]"));
        Assert.NotNull(rail.QuerySelector(".sidenav-foot-card [data-shortcuts-open]"));
        Assert.NotNull(rail.QuerySelector("[data-testid=rail-tour]"));
    }

    [GeneratedRegex("<button[^>]*data-theme-toggle[^>]*>")]
    private static partial Regex ThemeToggleTag();

    [GeneratedRegex("<script src=\"[^\"]*js/shell[^\"]*\\.js\"")]
    private static partial Regex ShellScript();

    [GeneratedRegex("href=\"[^\"]*lib/bootstrap/css/bootstrap[^\"]*\\.css\"")]
    private static partial Regex BootstrapCss();

    [GeneratedRegex("<button[^>]*data-nav-toggle[^>]*aria-controls=\"sidenav\"[^>]*aria-expanded=\"false\"")]
    private static partial Regex DrawerToggle();
}
