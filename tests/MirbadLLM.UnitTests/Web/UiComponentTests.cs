using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Ui;
using MirbadLLM.Web.Components.Layout;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>bUnit tests for the shared UI components: ConfirmDialog, StateBadge, StatTile, Sparkline, and the
/// shared number/state formatting in <see cref="UiFormat"/>.</summary>
public sealed class UiComponentTests
{
    [Theory]
    [InlineData("models", "rail-models")]
    [InlineData("nodes", "rail-node-count")]
    [InlineData("overview", "rail-generation")]
    public void RailCounts_WithoutSources_PrintsMissing(string kind, string testId)
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<RailCounts>(p => p.Add(x => x.Kind, kind));
        Assert.Equal(UiFormat.Missing, cut.Find($"[data-testid={testId}]").TextContent);
        Assert.Empty(cut.FindAll(".nav-meta--live"));
    }

    [Fact]
    public async Task RailCounts_ZeroAttentionIsHidden_AndSubscribersShareOneTimer()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddScoped<LiveDataService>();
        var first = ctx.Render<RailCounts>(p => p.Add(x => x.Kind, "overview"));
        var second = ctx.Render<RailCounts>(p => p.Add(x => x.Kind, "models"));
        Assert.Empty(first.FindAll("[data-testid=rail-attention]"));
        var live = ctx.Services.GetRequiredService<LiveDataService>();
        Assert.Equal(1, live.TimerStarts);
        Assert.Equal(1, live.Fetches);
        await first.Instance.DisposeAsync();
        await second.Instance.DisposeAsync();
        Assert.Equal(0, live.SubscriberCount);
    }

    // ---- ConfirmDialog ----

    [Fact]
    public void ConfirmDialog_Confirm_InvokesCallbackAndCloses()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        bool confirmed = false;
        bool? openAfter = null;

        var cut = ctx.Render<ConfirmDialog>(p => p
            .Add(x => x.Open, true)
            .Add(x => x.Title, "Delete demo?")
            .Add(x => x.ConfirmLabel, "Delete model")
            .Add(x => x.Destructive, true)
            .Add(x => x.OnConfirm, () => confirmed = true)
            .Add(x => x.OpenChanged, open => openAfter = open)
            .AddChildContent("<p>The checkpoint stays on disk.</p>"));

        var dialog = cut.Find("dialog");
        Assert.Equal("Delete demo?", cut.Find("h2").TextContent);
        Assert.Equal(cut.Find("h2").Id, dialog.GetAttribute("aria-labelledby"));
        Assert.Contains("stays on disk", cut.Find($"#{dialog.GetAttribute("aria-describedby")}").TextContent, StringComparison.Ordinal);
        Assert.Contains("btn-danger", cut.Find("[data-testid=confirm-dialog-confirm]").GetAttribute("class"), StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=confirm-dialog-cancel]").HasAttribute("autofocus"));

        cut.Find("[data-testid=confirm-dialog-confirm]").Click();

        Assert.True(confirmed);
        Assert.False(openAfter);
    }

    [Fact]
    public void ConfirmDialog_Cancel_DoesNotConfirm()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        bool confirmed = false;
        bool cancelled = false;

        var cut = ctx.Render<ConfirmDialog>(p => p
            .Add(x => x.Open, true)
            .Add(x => x.Title, "Revoke key?")
            .Add(x => x.ConfirmLabel, "Revoke key")
            .Add(x => x.OnConfirm, () => confirmed = true)
            .Add(x => x.OnCancel, () => cancelled = true));

        cut.Find("[data-testid=confirm-dialog-cancel]").Click();

        Assert.False(confirmed);
        Assert.True(cancelled);
        Assert.Equal("false", cut.Find("dialog").GetAttribute("data-open"));
    }

    [Fact]
    public void ConfirmDialog_EscapeCancel_ClosesWithoutConfirming()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        bool confirmed = false;

        var cut = ctx.Render<ConfirmDialog>(p => p
            .Add(x => x.Open, true)
            .Add(x => x.Title, "Start anyway?")
            .Add(x => x.ConfirmLabel, "Start anyway")
            .Add(x => x.OnConfirm, () => confirmed = true));

        cut.Find("dialog").TriggerEvent("oncancel", EventArgs.Empty);

        Assert.False(confirmed);
        Assert.Equal("false", cut.Find("dialog").GetAttribute("data-open"));
    }

    [Fact]
    public void ConfirmDialog_OpeningCallsShowModal()
    {
        using var ctx = new Bunit.BunitContext();
        var module = ctx.JSInterop.SetupModule("./interop.js");
        module.SetupVoid("showDialog", _ => true);

        ctx.Render<ConfirmDialog>(p => p
            .Add(x => x.Open, true)
            .Add(x => x.Title, "T")
            .Add(x => x.ConfirmLabel, "Go"));

        Assert.Single(module.Invocations, i => i.Identifier == "showDialog");
    }

    // ---- StateBadge ----

    [Theory]
    [InlineData(ModelState.Stopped, "state--stopped", "Stopped")]
    [InlineData(ModelState.Starting, "state--starting", "Starting")]
    [InlineData(ModelState.Ready, "state--ready", "Ready")]
    [InlineData(ModelState.Stopping, "state--stopping", "Stopping")]
    [InlineData(ModelState.Failed, "state--failed", "Failed")]
    public void StateBadge_PairsClassWithTextAndIcon(ModelState state, string cssClass, string text)
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<StateBadge>(p => p.Add(x => x.State, state).Add(x => x.TestId, "badge"));

        var badge = cut.Find("[data-testid=badge]");
        Assert.Contains(cssClass, badge.GetAttribute("class"), StringComparison.Ordinal);
        Assert.Contains(text, badge.TextContent, StringComparison.Ordinal);
        var svg = badge.QuerySelector("svg")!;
        Assert.Equal("true", svg.GetAttribute("aria-hidden"));
    }

    [Fact]
    public void StateBadge_StartingShowsElapsed_ReadyDoesNot()
    {
        using var ctx = new Bunit.BunitContext();
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        var starting = ctx.Render<StateBadge>(p => p
            .Add(x => x.State, ModelState.Starting)
            .Add(x => x.Since, now.AddSeconds(-125))
            .Add(x => x.Now, now)
            .Add(x => x.TestId, "s"));
        var ready = ctx.Render<StateBadge>(p => p
            .Add(x => x.State, ModelState.Ready)
            .Add(x => x.Since, now.AddSeconds(-125))
            .Add(x => x.Now, now)
            .Add(x => x.TestId, "r"));

        Assert.Equal("2m 05s", starting.Find("[data-testid=s-elapsed]").TextContent);
        Assert.Empty(ready.FindAll("[data-testid=r-elapsed]"));
    }

    // ---- StatTile ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("—")]
    public void StatTile_MissingValue_ShowsDashAndHidesUnit(string? value)
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<StatTile>(p => p
            .Add(x => x.Label, "TTFT p95")
            .Add(x => x.Value, value)
            .Add(x => x.Unit, "ms")
            .Add(x => x.TestId, "t"));

        Assert.Equal("—", cut.Find("[data-testid=t-value]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=t-unit]"));
        Assert.DoesNotContain("NaN", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void StatTile_ShowTrendFalse_HasNoTrendSlotOrEmptyText()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<StatTile>(p => p
            .Add(x => x.Label, "Memory used")
            .Add(x => x.Value, "110.5")
            .Add(x => x.ShowTrend, false)
            .Add(x => x.TestId, "t"));

        Assert.Empty(cut.FindAll("[data-testid=t-trend]"));
        Assert.DoesNotContain("Not enough data yet", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void StatTile_WithValue_ShowsUnitDetailAndTrend()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<StatTile>(p => p
            .Add(x => x.Label, "Generation")
            .Add(x => x.Value, "54.2")
            .Add(x => x.Unit, "tok/s")
            .Add(x => x.Detail, "Across 1 Ready model")
            .Add(x => x.Trend, new List<double?> { 10, null, 30, 54.2 })
            .Add(x => x.TestId, "t"));

        Assert.Equal("54.2", cut.Find("[data-testid=t-value]").TextContent);
        Assert.Equal("tok/s", cut.Find("[data-testid=t-unit]").TextContent);
        Assert.Equal("Across 1 Ready model", cut.Find("[data-testid=t-detail]").TextContent);
        var trend = cut.Find("[data-testid=t-trend]");
        Assert.Equal("img", trend.GetAttribute("role"));
        Assert.Contains("latest 54.2", trend.GetAttribute("aria-label"), StringComparison.Ordinal);
        // The null sample breaks the line into two segments.
        Assert.Equal(2, cut.Find("path").GetAttribute("d")!.Split(' ').Count(seg => seg.StartsWith('M')));
    }

    [Fact]
    public void StatTile_NoTrendParameter_StillRendersTheTrendSlot()
    {
        // Every tile keeps the same 28px sparkline slot so trend lines share one baseline, even when
        // the caller (e.g. a Monitoring host tile) never passes a Trend.
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<StatTile>(p => p
            .Add(x => x.Label, "Error rate")
            .Add(x => x.Value, "0")
            .Add(x => x.Unit, "%")
            .Add(x => x.TestId, "t"));

        Assert.Empty(cut.FindAll("svg"));
        Assert.NotNull(cut.Find("[data-testid=t-trend-empty]"));
        Assert.Equal("Not enough data yet", cut.Find("[data-testid=t-trend-empty]").TextContent);
    }

    [Fact]
    public void StatTile_CustomTrendEmptyText_IsUsedForTheEmptySlot()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<StatTile>(p => p
            .Add(x => x.Label, "Error rate")
            .Add(x => x.Value, "—")
            .Add(x => x.Unit, "%")
            .Add(x => x.Trend, new List<double?>())
            .Add(x => x.TrendEmptyText, "No requests yet")
            .Add(x => x.TestId, "t"));

        Assert.Equal("No requests yet", cut.Find("[data-testid=t-trend-empty]").TextContent);
    }

    [Fact]
    public void Sparkline_TooFewPoints_SaysSoInsteadOfDrawing()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<Sparkline>(p => p.Add(x => x.Values, new List<double?> { null, 5 }).Add(x => x.TestId, "sp"));

        Assert.Empty(cut.FindAll("svg"));
        Assert.Contains("Not enough data", cut.Find("[data-testid=sp-empty]").TextContent, StringComparison.Ordinal);
    }

    // ---- UiFormat ----

    [Fact]
    public void UiFormat_MissingValuesAreDashes()
    {
        Assert.Equal("—", UiFormat.Number(null));
        Assert.Equal("—", UiFormat.Number(double.NaN));
        Assert.Equal("—", UiFormat.GiB(null));
        Assert.Equal("—", UiFormat.Percent(double.NaN));
        Assert.Equal(("—", ""), UiFormat.Latency(null));
    }

    [Fact]
    public void UiFormat_LatencyPicksMillisecondsOrSeconds()
    {
        Assert.Equal(("412", "ms"), UiFormat.Latency(0.412));
        Assert.Equal(("1.24", "s"), UiFormat.Latency(1.2449));
    }

    [Fact]
    public void UiFormat_FailureSummary_PicksTheErrorLine()
    {
        string dump = "INFO starting\nValueError: max_model_len 300000 exceeds the model's 262144\nINFO exiting";
        Assert.Equal("ValueError: max_model_len 300000 exceeds the model's 262144", UiFormat.FailureSummary(dump));
        Assert.Equal("exit code 137", UiFormat.FailureSummary("exit code 137"));
        Assert.Equal("No details were reported.", UiFormat.FailureSummary(null));
    }

    [Fact]
    public void UiFormat_SpeculativeLabel_FormatsMtpAndNgramOrOff()
    {
        Assert.Equal("Off", UiFormat.SpeculativeLabel(null));
        Assert.Equal("MTP × 2", UiFormat.SpeculativeLabel(new MirbadLLM.Core.SpeculativeDecoding("mtp", 2)));
        Assert.Equal("n-gram × 4", UiFormat.SpeculativeLabel(new MirbadLLM.Core.SpeculativeDecoding("ngram", 4)));
    }

    [Fact]
    public void UiFormat_ContextLength_UsesTabularThousandsSeparators()
    {
        Assert.Equal("262,144", UiFormat.ContextLength(262_144));
    }

    [Fact]
    public void UiFormat_IsoUtcAndClockTimeUtc_AreAlwaysUtcRegardlessOfServerTimeZone()
    {
        var at = new DateTimeOffset(2026, 9, 29, 14, 5, 0, TimeSpan.FromHours(3)); // 11:05 UTC
        Assert.Equal("2026-09-29T11:05:00Z", UiFormat.IsoUtc(at));
        Assert.Equal("11:05 UTC", UiFormat.ClockTimeUtc(at));
    }

    // ---- JobProgressPanel ----

    private static JobSnapshot MakeJob(DateTimeOffset started, DateTimeOffset estimatedEnd, JobKind kind = JobKind.Benchmark) =>
        new("job-1", kind, "qwen", started, estimatedEnd, "Benchmarking: N=8, run 2 of 3", 2, 8, null, 0, 0, [], null, Cancelling: false);

    [Fact]
    public void JobProgressPanel_RendersAClientFormattedTimeElement_ForTheEstimatedEnd()
    {
        using var ctx = new Bunit.BunitContext();
        var now = new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 9, 29, 14, 5, 0, TimeSpan.Zero);
        var job = MakeJob(now.AddMinutes(-2), end);

        var cut = ctx.Render<JobProgressPanel>(p => p.Add(x => x.Job, job).Add(x => x.Now, now));

        var time = cut.Find("[data-testid=job-left-time]");
        Assert.Equal("hm", time.GetAttribute("data-fmt"));
        Assert.Equal("2026-09-29T14:05:00Z", time.GetAttribute("datetime"));
        Assert.Equal("14:05 UTC", time.TextContent);
        Assert.Contains("until ~", cut.Find("[data-testid=job-left]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void JobProgressPanel_PastTheEstimate_SaysSoInsteadOfATime()
    {
        using var ctx = new Bunit.BunitContext();
        var now = new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);
        var job = MakeJob(now.AddMinutes(-10), now.AddMinutes(-1));

        var cut = ctx.Render<JobProgressPanel>(p => p.Add(x => x.Job, job).Add(x => x.Now, now));

        Assert.Contains("finishing, longer than estimated", cut.Find("[data-testid=job-left]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=job-left-time]"));
    }

    // ---- RingGauge, LoadPips, NodeCard, Drawer (Monitoring) ----

    [Fact]
    public void RingGauge_Null_DrawsOnlyTheTrack_AndReadsADash()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<RingGauge>(p => p.Add(x => x.Percent, null).Add(x => x.Label, "Memory").Add(x => x.ShowValue, true).Add(x => x.TestId, "ring"));

        Assert.Equal("Memory: —", cut.Find("[data-testid=ring]").GetAttribute("aria-label"));
        Assert.Equal("—", cut.Find(".ring-text").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=ring-value]"));
        Assert.Single(cut.FindAll("circle"));
    }

    [Theory]
    [InlineData(0.0, "0%", 1.0)]
    [InlineData(100.0, "100%", 0.0)]
    [InlineData(50.0, "50%", 0.5)]
    [InlineData(140.0, "100%", 0.0)] // clamped, never drawn past full
    public void RingGauge_FillsInProportion_ZeroIsAnEmptyArcNotADash(double percent, string text, double unfilled)
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<RingGauge>(p => p.Add(x => x.Percent, percent).Add(x => x.Label, "KV cache").Add(x => x.TestId, "ring"));

        var arc = cut.Find("[data-testid=ring-value]");
        double circumference = double.Parse(arc.GetAttribute("stroke-dasharray")!, System.Globalization.CultureInfo.InvariantCulture);
        double offset = double.Parse(arc.GetAttribute("stroke-dashoffset")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(unfilled, offset / circumference, precision: 3);
        Assert.Equal($"KV cache: {text}", cut.Find("[data-testid=ring]").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".ring-text")); // without ShowValue the caller prints the number
    }

    [Fact]
    public void LoadPips_DrawRunningThenWaiting_CappedAtSixteen_AndAlwaysPrintTheNumbers()
    {
        using var ctx = new Bunit.BunitContext();

        var few = ctx.Render<LoadPips>(p => p.Add(x => x.Running, 2).Add(x => x.Waiting, 1));
        Assert.Equal(2, few.FindAll(".pip--run").Count);
        Assert.Single(few.FindAll(".pip--wait"));
        Assert.Equal(8, few.FindAll(".pip").Count); // eight slots at least
        Assert.Equal("2 · 1", few.Find(".num").TextContent);

        var many = ctx.Render<LoadPips>(p => p.Add(x => x.Running, 12).Add(x => x.Waiting, 30));
        Assert.Equal(12, many.FindAll(".pip--run").Count);
        Assert.Equal(LoadPips.MaxPips - 12, many.FindAll(".pip--wait").Count);
        Assert.Equal("12 · 30", many.Find(".num").TextContent);

        var unknown = ctx.Render<LoadPips>(p => p.Add(x => x.Running, double.NaN).Add(x => x.Waiting, double.NaN));
        Assert.Empty(unknown.FindAll(".pip--run"));
        Assert.Equal("— · —", unknown.Find(".num").TextContent);
    }

    [Fact]
    public void NodeCard_StateIsIconAndWord_BeadsAreCountedInWords_AndPressedIsAToggle()
    {
        using var ctx = new Bunit.BunitContext();
        int clicks = 0;
        NodeCard.Bead[] beads = [new("Model-A", "var(--ss-cat-1)", true), new("Model-B", "var(--ss-cat-2)", false)];

        var cut = ctx.Render<NodeCard>(p => p
            .Add(x => x.Name, "dgx")
            .Add(x => x.State, NodeState.Offline)
            .Add(x => x.MemoryPercent, 77)
            .Add(x => x.Meta, "GPU 96% · 75°C · 46 W")
            .Add(x => x.Beads, beads)
            .Add(x => x.Pressed, true)
            .Add(x => x.OnClick, () => clicks++)
            .Add(x => x.TestId, "nd"));

        var button = cut.Find("[data-testid=nd]");
        Assert.Equal("button", button.TagName, ignoreCase: true);
        Assert.Equal("true", button.GetAttribute("aria-pressed"));
        Assert.Equal("Offline", cut.Find("[data-testid=nd-state]").TextContent.Trim());
        Assert.NotNull(cut.Find("[data-testid=nd-state] svg"));
        Assert.Equal("77%", cut.Find(".ring-text").TextContent);
        Assert.Equal("1 running · 1 off", cut.Find("[data-testid=nd-count]").TextContent);
        Assert.Equal("background:var(--ss-cat-1)", cut.Find(".bead--on").GetAttribute("style"));
        Assert.Null(cut.Find(".bead--off").GetAttribute("style"));
        Assert.Contains("Model-B · not running", cut.Find(".bead--off").GetAttribute("title"), StringComparison.Ordinal);

        button.Click();
        Assert.Equal(1, clicks);
    }

    [Fact]
    public async Task Drawer_Closed_RendersNothingInside_Open_EscAndTheButtonClose()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        int closes = 0;

        var cut = ctx.Render<Drawer>(p => p
            .Add(x => x.Open, false)
            .Add(x => x.TitleId, "t")
            .Add(x => x.Header, "<h2 id=\"t\">Model-A</h2>")
            .Add(x => x.ChildContent, "<p>readout</p>")
            .Add(x => x.OnClose, () => closes++));

        var aside = cut.Find("aside");
        Assert.Equal("true", aside.GetAttribute("aria-hidden"));
        Assert.Empty(cut.FindAll("p"));

        cut.Render(p => p.Add(x => x.Open, true));

        aside = cut.Find("aside");
        Assert.Null(aside.GetAttribute("aria-hidden"));
        Assert.Equal("t", aside.GetAttribute("aria-labelledby"));
        Assert.Contains("side-panel--open", aside.GetAttribute("class"), StringComparison.Ordinal);
        Assert.Equal("readout", cut.Find("p").TextContent);
        Assert.Equal("Close details", cut.Find("[data-testid=drawer-close]").GetAttribute("aria-label"));

        aside.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Assert.Equal(0, closes);
        aside.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(1, closes);
        cut.Find("[data-testid=drawer-close]").Click();
        Assert.Equal(2, closes);

        // Opening asks interop.js to watch for a press outside; that press closes it like the button.
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "closeOnPressOutside");
        await cut.InvokeAsync(() => cut.Instance.PressedOutside());
        Assert.Equal(3, closes);
    }
}
