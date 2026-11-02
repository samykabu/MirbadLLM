using Bunit;
using MirbadLLM.Web.Components.Widgets;

namespace MirbadLLM.UnitTests.Web;

/// <summary>bUnit tests for the reusable inline-SVG <see cref="LineChart"/> component: it draws a path for
/// a series' points and breaks the path (a new "M" segment) across null gaps.</summary>
public sealed class LineChartTests
{
    [Fact]
    public void RendersPathForSeriesPoints()
    {
        using var ctx = new Bunit.BunitContext();
        var now = DateTimeOffset.UtcNow;
        var series = new List<LineChartSeries>
        {
            new("A", "red", [
                new LineChartPoint(now, 1),
                new LineChartPoint(now.AddSeconds(5), 2),
                new LineChartPoint(now.AddSeconds(10), 3),
            ]),
        };

        var cut = ctx.Render<LineChart>(parameters => parameters
            .Add(p => p.Series, series)
            .Add(p => p.Title, "Test"));

        var path = cut.Find("path").GetAttribute("d");
        Assert.NotNull(path);
        Assert.StartsWith("M", path);
        Assert.Contains("L", path);
    }

    [Fact]
    public void SplitsPathAcrossNullGaps()
    {
        using var ctx = new Bunit.BunitContext();
        var now = DateTimeOffset.UtcNow;
        var series = new List<LineChartSeries>
        {
            new("A", "red", [
                new LineChartPoint(now, 1),
                new LineChartPoint(now.AddSeconds(5), null),
                new LineChartPoint(now.AddSeconds(10), 3),
            ]),
        };

        var cut = ctx.Render<LineChart>(parameters => parameters
            .Add(p => p.Series, series)
            .Add(p => p.Title, "Test"));

        var d = cut.Find("path").GetAttribute("d")!;
        var moveCommandCount = d.Split(' ').Count(segment => segment.StartsWith('M'));
        Assert.Equal(2, moveCommandCount);
    }

    [Fact]
    public void ANonFiniteValueIsAGapNotANaNCoordinate()
    {
        // MN-230: llama.cpp reports no KV-cache usage, so Monitoring fed the chart NaN. Min/Max over NaN made the
        // bounds NaN, every coordinate NaN, the path invalid ("M12,NaN") and the data table read "NaN".
        using var ctx = new Bunit.BunitContext();
        var now = DateTimeOffset.UtcNow;
        var series = new List<LineChartSeries>
        {
            new("KV cache %", "red", [
                new LineChartPoint(now, 10),
                new LineChartPoint(now.AddSeconds(5), double.NaN),
                new LineChartPoint(now.AddSeconds(10), double.PositiveInfinity),
                new LineChartPoint(now.AddSeconds(15), 30),
            ]),
        };
        Assert.Null(series[0].Points[1].Value);
        Assert.Null(series[0].Points[2].Value);

        var cut = ctx.Render<LineChart>(parameters => parameters
            .Add(p => p.Series, series)
            .Add(p => p.Title, "KV cache"));

        string d = cut.Find("path").GetAttribute("d")!;
        Assert.DoesNotContain("NaN", d, StringComparison.Ordinal);
        Assert.DoesNotContain("∞", d, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(2, d.Split(' ').Count(segment => segment.StartsWith('M'))); // one gap in the middle
    }

    [Fact]
    public void EmptySeriesShowsNoDataInsteadOfAPath()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<LineChart>(parameters => parameters
            .Add(p => p.Series, new List<LineChartSeries>())
            .Add(p => p.Title, "Empty"));

        Assert.Empty(cut.FindAll("path"));
        Assert.Contains("No samples in this window", cut.Markup);
    }

    [Fact]
    public void EmptyPlotSaysWhatIsMissingWhenToldWhy()
    {
        using var ctx = new Bunit.BunitContext();
        var now = DateTimeOffset.UtcNow;

        var cut = ctx.Render<LineChart>(parameters => parameters
            .Add(p => p.Series, new List<LineChartSeries> { new("GPU %", "red", [new LineChartPoint(now, null)]) })
            .Add(p => p.Title, "GPU utilization")
            .Add(p => p.EmptyText, "dgx reports no GPU utilization"));

        Assert.Equal("dgx reports no GPU utilization", cut.Find("[data-testid=line-chart-gpu-utilization-empty]").TextContent);
    }

    [Fact]
    public void ScreenReaderTableIsInsideAVisuallyHiddenWrapperSoItCannotWidenThePage()
    {
        using var ctx = new Bunit.BunitContext();
        var now = DateTimeOffset.UtcNow;

        var cut = ctx.Render<LineChart>(parameters => parameters
            .Add(p => p.Series, new List<LineChartSeries> { new("A", "red", [new LineChartPoint(now, 1)]) })
            .Add(p => p.Title, "Wide"));

        // A table box ignores width:1px/overflow:hidden, so the hiding must be on a block wrapper, not on the table.
        var table = cut.Find("[data-testid=line-chart-wide-table]");
        Assert.DoesNotContain("visually-hidden", table.ClassName ?? "", StringComparison.Ordinal);
        Assert.Equal("DIV", table.ParentElement!.TagName);
        Assert.Contains("visually-hidden", table.ParentElement.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void MinMaxLabelsReflectSeriesValues()
    {
        using var ctx = new Bunit.BunitContext();
        var now = DateTimeOffset.UtcNow;
        var series = new List<LineChartSeries>
        {
            new("A", "red", [new LineChartPoint(now, 10), new LineChartPoint(now.AddSeconds(1), 50)]),
        };

        var cut = ctx.Render<LineChart>(parameters => parameters
            .Add(p => p.Series, series)
            .Add(p => p.Title, "Bounds"));

        Assert.Contains("10", cut.Find("[data-testid=line-chart-bounds-min]").TextContent);
        Assert.Contains("50", cut.Find("[data-testid=line-chart-bounds-max]").TextContent);
    }
}
