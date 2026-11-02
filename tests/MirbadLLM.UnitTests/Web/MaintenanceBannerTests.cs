using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Web.Components.Layout;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>bUnit tests for <see cref="MaintenanceBanner"/>: it renders a client-formatted <c>&lt;time&gt;</c>
/// for "until ~HH:MM" (see UiFormat.IsoUtc/ClockTimeUtc and wwwroot/js/shell.js's formatLocalTimes) instead of
/// a plain string, since the controller always runs in UTC and only the browser knows the viewer's own zone.</summary>
public sealed class MaintenanceBannerTests
{
    private sealed class FakeMaintenanceMode(MaintenanceState? current) : IMaintenanceMode
    {
        public MaintenanceState? Current { get; } = current;
    }

    [Fact]
    public void NoActiveWindow_RendersNothing()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton<IMaintenanceMode>(new FakeMaintenanceMode(null));

        var cut = ctx.Render<MaintenanceBanner>();

        Assert.Empty(cut.FindAll("[data-testid=maintenance-banner]"));
    }

    [Fact]
    public void ActiveWindow_RendersAClientFormattedTimeElement_WithAUtcExplicitFallback()
    {
        using var ctx = new Bunit.BunitContext();
        var end = new DateTimeOffset(2026, 9, 29, 14, 5, 0, TimeSpan.Zero);
        var window = new MaintenanceState("job-1", "tuning", "qwen", DateTimeOffset.UtcNow, end);
        ctx.Services.AddSingleton<IMaintenanceMode>(new FakeMaintenanceMode(window));

        var cut = ctx.Render<MaintenanceBanner>();

        Assert.NotNull(cut.Find("[data-testid=maintenance-banner]"));
        var time = cut.Find("[data-testid=maintenance-banner] time");
        Assert.Equal("hm", time.GetAttribute("data-fmt"));
        Assert.Equal("2026-09-29T14:05:00Z", time.GetAttribute("datetime"));
        // Server-rendered fallback text, replaced client-side by shell.js's formatLocalTimes.
        Assert.Equal("14:05 UTC", time.TextContent);
        Assert.Contains("tuning", cut.Find("[data-testid=maintenance-banner]").TextContent, StringComparison.Ordinal);
    }
}
