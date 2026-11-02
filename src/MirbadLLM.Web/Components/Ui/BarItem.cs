namespace MirbadLLM.Web.Components.Ui;

/// <summary>One row of <c>BarList</c>: a label, the value that sizes the bar, and its formatted text.</summary>
/// <param name="Highlight">The row to call out (e.g. the tuning winner); drawn with a trophy icon, not colour alone.</param>
/// <param name="Muted">A row that does not count (e.g. a rejected candidate).</param>
/// <param name="Secondary">Draw the bar in the second series style (dashed outline), for a compared run.</param>
public sealed record BarItem(string Label, double? Value, string Display, bool Highlight = false, bool Muted = false, bool Secondary = false);
