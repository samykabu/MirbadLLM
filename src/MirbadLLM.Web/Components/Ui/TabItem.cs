namespace MirbadLLM.Web.Components.Ui;

/// <summary>One tab in a <see cref="Tabs"/> tablist: an id (also the URL query value and the fragment used
/// to build stable DOM ids), a label, an optional small status badge (text, never color alone — see
/// DESIGN.md's Paired State Rule), and a count shown as a small pill next to the label. The count is read
/// as "Managed, 11 services": a visually hidden separator, then <see cref="CountNoun"/> when given.</summary>
public sealed record TabItem(
    string Id,
    string Label,
    int? Count = null,
    string? BadgeText = null,
    string? BadgeIcon = null,
    string? BadgeClass = null,
    bool Disabled = false,
    string? CountNoun = null);

/// <summary>The two tab looks: a page's main tabs, and a lighter second level nested inside one.</summary>
public enum TabsVariant
{
    Line,
    Segmented,
}
