namespace MirbadLLM.Web.Components.Ui;

/// <summary>How a legend entry's swatch is drawn; line series alternate solid and dashed so colour is never the only cue.</summary>
public enum LegendSwatch
{
    Line,
    Dashed,
    Dotted,
    Block,
    Outline,
}

/// <summary>One legend entry. <paramref name="Color"/> is any CSS colour, normally a <c>var(--ss-series-*)</c> token.</summary>
public sealed record LegendItem(string Label, string Color, LegendSwatch Swatch = LegendSwatch.Line, string? Value = null);
