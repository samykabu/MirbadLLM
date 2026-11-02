namespace MirbadLLM.Web.Components.Ui;

/// <summary>SVG path data for <see cref="Icon"/>, on a 24×24 grid. Shapes follow the common open stroke-icon
/// vocabulary (grid, message, pulse, server, key, plug…), authored here so no icon package is needed.</summary>
public static class IconPaths
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["dashboard"] = """<rect x="3" y="3" width="7" height="9" rx="1"/><rect x="14" y="3" width="7" height="5" rx="1"/><rect x="14" y="12" width="7" height="9" rx="1"/><rect x="3" y="16" width="7" height="5" rx="1"/>""",
        ["chat"] = """<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>""",
        ["activity"] = """<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>""",
        ["server"] = """<rect x="3" y="3" width="18" height="7" rx="2"/><rect x="3" y="14" width="18" height="7" rx="2"/><path d="M7 6.5h.01M7 17.5h.01"/>""",
        ["key"] = """<circle cx="7.5" cy="15.5" r="4.5"/><path d="m10.7 12.3 9.8-9.8M16 7l3 3M18.5 4.5l2 2"/>""",
        ["plug"] = """<path d="M12 22v-5M9 8V2M15 8V2M18 8v4a6 6 0 0 1-6 6 6 6 0 0 1-6-6V8Z"/>""",
        ["plus"] = """<path d="M12 5v14M5 12h14"/>""",
        ["play"] = """<path d="M7 4v16l13-8Z"/>""",
        ["stop"] = """<rect x="6" y="6" width="12" height="12" rx="1.5"/>""",
        ["logs"] = """<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8Z"/><path d="M14 3v5h5M9 13h6M9 17h6M9 9h1"/>""",
        ["edit"] = """<path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L8 18l-4 1 1-4Z"/>""",
        ["sun"] = """<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>""",
        ["moon"] = """<path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5Z"/>""",
        ["logout"] = """<path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9"/>""",
        ["menu"] = """<path d="M4 6h16M4 12h16M4 18h16"/>""",
        ["close"] = """<path d="M18 6 6 18M6 6l12 12"/>""",
        ["state-ready"] = """<circle cx="12" cy="12" r="9"/><path d="m8.5 12 2.5 2.5 4.5-5"/>""",
        ["state-starting"] = """<path d="M21 12a9 9 0 1 1-6.2-8.6"/>""",
        ["state-stopping"] = """<circle cx="12" cy="12" r="9"/><rect x="9" y="9" width="6" height="6" rx="1"/>""",
        ["state-stopped"] = """<circle cx="12" cy="12" r="9"/><path d="M8 12h8"/>""",
        ["state-failed"] = """<path d="M7.9 2h8.2L22 7.9v8.2L16.1 22H7.9L2 16.1V7.9Z"/><path d="M12 8v4.5M12 16h.01"/>""",
        ["warning"] = """<path d="M10.3 3.9 1.9 18a2 2 0 0 0 1.7 3h16.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/><path d="M12 9v4M12 17h.01"/>""",
        ["info"] = """<circle cx="12" cy="12" r="9"/><path d="M12 16v-4.5M12 8h.01"/>""",
        ["copy"] = """<rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2"/>""",
        ["check"] = """<path d="M20 6 9 17l-5-5"/>""",
        ["download"] = """<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3"/>""",
        ["folder"] = """<path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.7-.9l-.8-1.2A2 2 0 0 0 7.9 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/>""",
        ["trash"] = """<path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M10 11v6M14 11v6"/>""",
        ["send"] = """<path d="m22 2-7 20-4-9-9-4Z"/><path d="M22 2 11 13"/>""",
        ["back"] = """<path d="m12 19-7-7 7-7M19 12H5"/>""",
        ["chevron-down"] = """<path d="m6 9 6 6 6-6"/>""",
        ["chevron-left"] = """<path d="m15 18-6-6 6-6"/>""",
        ["chevron-right"] = """<path d="m9 18 6-6-6-6"/>""",
        ["chevrons-left"] = """<path d="m11 17-5-5 5-5M18 17l-5-5 5-5"/>""",
        ["chevrons-right"] = """<path d="m6 17 5-5-5-5M13 17l5-5-5-5"/>""",
        ["more"] = """<circle cx="5" cy="12" r="1.25"/><circle cx="12" cy="12" r="1.25"/><circle cx="19" cy="12" r="1.25"/>""",
        ["gauge"] = """<path d="M12 14l4-4"/><path d="M3.3 17a9 9 0 1 1 17.4 0"/><circle cx="12" cy="14" r="1.5"/>""",
        ["refresh"] = """<path d="M21 12a9 9 0 0 1-15.5 6.2L3 16"/><path d="M3 12a9 9 0 0 1 15.5-6.2L21 8"/><path d="M21 3v5h-5M3 21v-5h5"/>""",
        ["pause"] = """<rect x="6" y="5" width="4" height="14" rx="1"/><rect x="14" y="5" width="4" height="14" rx="1"/>""",
        ["trophy"] = """<path d="M8 21h8M12 17v4M7 4h10v5a5 5 0 0 1-10 0Z"/><path d="M17 5h3v2a3 3 0 0 1-3 3M7 5H4v2a3 3 0 0 0 3 3"/>""",
        ["sliders"] = """<path d="M21 4h-7M10 4H3M21 12h-9M8 12H3M21 20h-5M12 20H3M14 2v4M8 10v4M16 18v4"/>""",
        ["power"] = """<path d="M12 3v9"/><path d="M18.4 6.6a9 9 0 1 1-12.8 0"/>""",
        ["restart"] = """<path d="M3 12a9 9 0 1 0 2.6-6.4L3 8"/><path d="M3 3v5h5"/>""",
        ["clock"] = """<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>""",
        ["keyboard"] = """<rect x="2" y="6" width="20" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/>""",
        ["arrow-right"] = """<path d="M5 12h14M13 5l7 7-7 7"/>""",
        ["wifi-off"] = """<path d="m2 2 20 20"/><path d="M8.5 16.4a5 5 0 0 1 7 0M5 12.9a10 10 0 0 1 5.2-2.8M19 12.9a10 10 0 0 0-2.1-1.6M2 8.8a15 15 0 0 1 4.2-2.6M22 8.8A15 15 0 0 0 10.7 5M12 20h.01"/>""",
        ["tag"] = """<path d="M12.6 2.6A2 2 0 0 0 11.2 2H4a2 2 0 0 0-2 2v7.2a2 2 0 0 0 .6 1.4l8.7 8.7a2.4 2.4 0 0 0 3.4 0l6.6-6.6a2.4 2.4 0 0 0 0-3.4z"/><circle cx="7.5" cy="7.5" r="1"/>""",
        ["archive"] = """<rect x="2" y="3" width="20" height="5" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8M10 12h4"/>""",
        ["upload"] = """<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12"/>""",
        ["lock"] = """<rect x="4.5" y="11" width="15" height="10" rx="2"/><path d="M8 11V7.5a4 4 0 0 1 8 0V11"/>""",
        ["nodes"] = """<rect x="9" y="2" width="6" height="6" rx="1"/><rect x="2" y="16" width="6" height="6" rx="1"/><rect x="16" y="16" width="6" height="6" rx="1"/><path d="M12 8v4M5 16v-2a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v2"/>""",
        ["compass"] = """<circle cx="12" cy="12" r="9"/><path d="m15.5 8.5-2 5-5 2 2-5Z"/>""",
        ["terminal"] = """<rect x="2.5" y="4" width="19" height="16" rx="2"/><path d="m6.5 9 3 3-3 3M12 15h5"/>""",
        ["shield"] = """<path d="M12 21s7-3.2 7-9V5.5L12 3 5 5.5V12c0 5.8 7 9 7 9Z"/><path d="m9 12 2 2 4-4"/>""",
        ["layers"] = """<path d="m12 3 9 5-9 5-9-5Z"/><path d="m3 13 9 5 9-5"/>""",
    };

    /// <summary>The inner SVG markup for <paramref name="name"/>; empty for an unknown name.</summary>
    public static string For(string name) => Paths.GetValueOrDefault(name, "");

    public static bool Has(string name) => Paths.ContainsKey(name);
}
