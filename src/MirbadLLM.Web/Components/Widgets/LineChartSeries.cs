namespace MirbadLLM.Web.Components.Widgets;

/// <summary>One point on a <see cref="LineChartSeries"/>. A null <see cref="Value"/> is a gap (no data for
/// that sample), rendered as a break in the line rather than interpolated.</summary>
public sealed record LineChartPoint(DateTimeOffset Time, double? Value)
{
    /// <summary>
    /// The value, or null for a gap. A non-finite value (NaN or infinity) is a gap too: a KPI a runtime does not
    /// report is NaN (llama.cpp has no KV-cache usage, MN-223), and one NaN in the bounds made every coordinate NaN,
    /// so the chart drew an invalid path and its table read "NaN" (MN-230).
    /// </summary>
    public double? Value { get; init; } = Value is { } value && double.IsFinite(value) ? value : null;
}

/// <summary>How a series' line is stroked. <see cref="Auto"/> alternates solid and dashed by position, so a
/// second series is never told apart by colour alone.</summary>
public enum LineStyle
{
    Auto,
    Solid,
    Dashed,
    Dotted,
}

/// <summary>One line on the <c>LineChart</c> component: a name, an SVG stroke color, and its points.</summary>
public sealed record LineChartSeries(string Name, string Color, IReadOnlyList<LineChartPoint> Points)
{
    public LineStyle Style { get; init; } = LineStyle.Auto;
}
