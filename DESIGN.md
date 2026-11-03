---
name: MirbadLLM
description: MirbadLLM, a graphite-and-amber console for a small model fleet; light and dark themes, the cube M as the mark.
colors:
  # Dark theme values; light-theme values carry the -light suffix. Light is the brand-first theme; both ship.
  page: "#15181b"
  panel: "#1e2226"
  elevated: "#272c31"
  sunken: "#101315"
  hover: "#272c31"
  overlay: "rgb(0 0 0 / 0.62)"
  text: "#eeefec"
  text-muted: "#9aa3aa"
  text-on-accent: "#15181b"
  border: "#2d3237"
  border-control: "#6e7880"
  accent: "#f2a900"
  accent-hover: "#ffb72b"
  accent-text: "#f2b42e"
  accent-subtle: "#2e2614"
  focus: "#ffb72b"
  ready: "#5fd08e"
  ready-subtle: "#11261a"
  busy: "#fb923c"
  busy-subtle: "#2e1c10"
  failed: "#f07a82"
  failed-subtle: "#331a1d"
  idle: "#9aa3aa"
  idle-subtle: "#272c31"
  series-1: "#f2a900"
  series-2: "#ffd27a"
  series-other: "#6e7880"
  series-reserve: "#4a5157"
  cat-1: "#f2a900"
  cat-2: "#2ab7a9"
  cat-3: "#e07fe0"
  cat-4: "#a79cf5"
  cat-5: "#c8a97e"
  cat-6: "#8fa3bd"
  cat-7: "#a9b85a"
  cat-8: "#9aa6d6"
  danger-fill: "#b3363e"
  danger-fill-hover: "#962c33"
  on-danger: "#ffffff"
  page-light: "#f7f7f5"
  panel-light: "#ffffff"
  elevated-light: "#efefec"
  sunken-light: "#f0f0ed"
  hover-light: "#f0f0ec"
  overlay-light: "rgb(30 34 38 / 0.45)"
  text-light: "#1e2226"
  text-muted-light: "#5f676e"
  text-on-accent-light: "#1e2226"
  border-light: "#e1e3e0"
  border-control-light: "#7f8a92"
  accent-light: "#f2a900"
  accent-hover-light: "#ffb72b"
  accent-text-light: "#8a5a00"
  accent-subtle-light: "#fdf1d3"
  focus-light: "#a87400"
  ready-light: "#25784a"
  ready-subtle-light: "#e4f3ea"
  busy-light: "#ae3b0a"
  busy-subtle-light: "#fdebdf"
  failed-light: "#b3363e"
  failed-subtle-light: "#fbe8e9"
  idle-light: "#5f676e"
  idle-subtle-light: "#efefec"
  series-1-light: "#b07a00"
  series-2-light: "#7a5200"
  series-other-light: "#8a949c"
  series-reserve-light: "#b4bbc1"
  cat-1-light: "#b07a00"
  cat-2-light: "#0f766e"
  cat-3-light: "#a21caf"
  cat-4-light: "#5e54b0"
  cat-5-light: "#8a6d3b"
  cat-6-light: "#4b5d73"
  cat-7-light: "#5f6b17"
  cat-8-light: "#4f5d95"
typography:
  display:
    fontFamily: "Plus Jakarta Sans, Inter, system-ui, -apple-system, Segoe UI, Roboto, Helvetica Neue, Arial, sans-serif"
    fontSize: "1.5rem"
    fontWeight: 500
    lineHeight: 1.2
    letterSpacing: "-0.01em"
  headline:
    fontFamily: "Plus Jakarta Sans, Inter, system-ui, sans-serif"
    fontSize: "1.0625rem"
    fontWeight: 600
    lineHeight: 1.35
    letterSpacing: "-0.005em"
  title:
    fontFamily: "Plus Jakarta Sans, Inter, system-ui, sans-serif"
    fontSize: "0.875rem"
    fontWeight: 600
    lineHeight: 1.4
  body:
    fontFamily: "Inter, system-ui, sans-serif"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: 1.5
  body-small:
    fontFamily: "Inter, system-ui, sans-serif"
    fontSize: "0.75rem"
    fontWeight: 400
    lineHeight: 1.5
  stat:
    fontFamily: "Azeret Mono, ui-monospace, SF Mono, Cascadia Mono, Menlo, Consolas, monospace"
    fontSize: "1.25rem"
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: "-0.01em"
    fontFeature: "\"tnum\" 1"
  data:
    fontFamily: "Azeret Mono, ui-monospace, monospace"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: 1.5
    letterSpacing: "-0.01em"
    fontFeature: "\"tnum\" 1"
  control:
    fontFamily: "Inter, system-ui, sans-serif"
    fontSize: "0.8125rem"
    fontWeight: 500
    letterSpacing: "-0.01em"
  label:
    fontFamily: "Inter, system-ui, sans-serif"
    fontSize: "0.75rem"
    fontWeight: 500
    letterSpacing: "0.09em"
  label-small:
    fontFamily: "Inter, system-ui, sans-serif"
    fontSize: "0.6875rem"
    fontWeight: 500
    letterSpacing: "0.08em"
  card-identity:
    fontFamily: "Azeret Mono, ui-monospace, monospace"
    fontSize: "0.9375rem"
    fontWeight: 600
  compact-stat:
    fontFamily: "Azeret Mono, ui-monospace, monospace"
    fontSize: "1rem"
    fontWeight: 600
  vital:
    fontFamily: "Azeret Mono, ui-monospace, monospace"
    fontSize: "26px"
    fontWeight: 600
  fleet-stat:
    fontFamily: "Azeret Mono, ui-monospace, monospace"
    fontSize: "38px"
    fontWeight: 600
  brand:
    fontFamily: "Plus Jakarta Sans, system-ui, sans-serif"
    fontSize: "1.25rem"
    fontWeight: 800
    letterSpacing: "-0.02em"
  login-brand:
    fontFamily: "Plus Jakarta Sans, system-ui, sans-serif"
    fontSize: "2rem"
    fontWeight: 800
    letterSpacing: "-0.02em"
rounded:
  # Tiny marks and tracks use glyph radii; controls and panels start at sm.
  hairline: "2px"
  glyph: "3px"
  compact: "4px"
  sm: "6px"
  md: "8px"
  lg: "12px"
  pill: "999px"
spacing:
  sp-1: "4px"
  sp-2: "8px"
  sp-3: "12px"
  sp-4: "16px"
  sp-5: "24px"
  sp-6: "32px"
  sp-7: "48px"
  control-h: "32px"
  touch-h: "44px"
  statusbar-h: "48px"
  sidenav-w: "240px"
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.text-on-accent}"
    typography: "{typography.control}"
    rounded: "{rounded.md}"
    padding: "5px 12px"
    height: "32px"
  button-primary-hover:
    backgroundColor: "{colors.accent-hover}"
    textColor: "{colors.text-on-accent}"
  button-default:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.text}"
    typography: "{typography.control}"
    rounded: "{rounded.md}"
    padding: "5px 12px"
    height: "32px"
  button-default-hover:
    backgroundColor: "{colors.hover}"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.text-muted}"
    typography: "{typography.control}"
    rounded: "{rounded.md}"
  button-ghost-hover:
    backgroundColor: "{colors.hover}"
    textColor: "{colors.text}"
  button-destructive-outline:
    backgroundColor: "transparent"
    textColor: "{colors.failed}"
    typography: "{typography.control}"
    rounded: "{rounded.md}"
    padding: "5px 12px"
  button-destructive-outline-hover:
    backgroundColor: "{colors.failed-subtle}"
  button-danger-confirm:
    backgroundColor: "{colors.danger-fill}"
    textColor: "{colors.on-danger}"
    typography: "{typography.control}"
    rounded: "{rounded.md}"
  button-danger-confirm-hover:
    backgroundColor: "{colors.danger-fill-hover}"
  input:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.text}"
    typography: "{typography.body}"
    rounded: "{rounded.md}"
    padding: "5px 12px"
    height: "32px"
  input-disabled:
    backgroundColor: "{colors.sunken}"
    textColor: "{colors.text-muted}"
  panel:
    backgroundColor: "{colors.panel}"
    rounded: "{rounded.lg}"
    padding: "16px"
  nav-link:
    backgroundColor: "transparent"
    textColor: "{colors.text-muted}"
    typography: "{typography.control}"
    rounded: "{rounded.md}"
    padding: "6px 8px"
    height: "32px"
  nav-link-active:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
  state-badge-ready:
    backgroundColor: "{colors.ready-subtle}"
    textColor: "{colors.ready}"
    typography: "{typography.label}"
    rounded: "{rounded.sm}"
    padding: "2px 9px 2px 7px"
    height: "24px"
  state-badge-busy:
    backgroundColor: "{colors.busy-subtle}"
    textColor: "{colors.busy}"
    rounded: "{rounded.sm}"
  state-badge-failed:
    backgroundColor: "{colors.failed-subtle}"
    textColor: "{colors.failed}"
    rounded: "{rounded.sm}"
  state-badge-stopped:
    backgroundColor: "{colors.idle-subtle}"
    textColor: "{colors.idle}"
    rounded: "{rounded.sm}"
  chip-alert-failed:
    backgroundColor: "{colors.failed-subtle}"
    textColor: "{colors.failed}"
    typography: "{typography.label}"
    rounded: "{rounded.sm}"
    padding: "2px 10px"
    height: "28px"
  chip-alert-busy:
    backgroundColor: "{colors.busy-subtle}"
    textColor: "{colors.busy}"
    rounded: "{rounded.sm}"
  tag-chip:
    backgroundColor: "{colors.sunken}"
    textColor: "{colors.text-muted}"
    typography: "{typography.body-small}"
    rounded: "{rounded.pill}"
    padding: "0 8px"
    height: "20px"
  stat-tile:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.text}"
    typography: "{typography.stat}"
    padding: "12px 16px"
  tab-line:
    backgroundColor: "transparent"
    textColor: "{colors.text-muted}"
    typography: "{typography.control}"
    padding: "8px 12px"
    height: "40px"
  tab-line-selected:
    textColor: "{colors.text}"
  tour-card:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    rounded: "{rounded.md}"
    padding: "16px"
  tip:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    rounded: "{rounded.md}"
    padding: "12px 12px 12px 16px"
---

# Design System: MirbadLLM

## Overview

**Creative North Star: "The Yard"**

MirbadLLM is the yard where a fleet of models is gathered, placed and tended (مربض is the Arabic word for the pen where a flock is kept). The console is graphite and warm paper with one amber, like signal lights on a well-run yard. It keeps the structure that makes a fleet readable (a persistent rail and status strip, every model and node one place with its own tabs). Both themes are first-class; the brand is presented light and dark equally.

Colour carries meaning only. Amber is action, selection, focus and the leading data series; green is Ready; orange is Starting, Stopping and anything that needs the operator; red is Failed and destruction. Everything else is paper, graphite, steel and text. Type splits by job: Plus Jakarta Sans, the face of the wordmark, for the wordmark and headings; Inter for everything else a person reads or operates (prose, navigation, buttons, tabs, labels); Azeret Mono for what the machine measures (figures, identifiers, state readouts, logs, code).

Density is a working console's. A 14px base, a 32px control height (44px on touch), 4/8 spacing and tracked caps as panel headers keep a whole fleet readable in one viewport; measurements are tabular monospace with "—" wherever a value is missing.

**Key Characteristics:**
- Warm paper (light) and graphite (dark), each in three steps plus a sunken well.
- One amber with one job; state is never colour alone (icon shape + label).
- Soft corners: 12px panels and dialogs, 8px controls, 6px chips.
- Plus Jakarta Sans for the wordmark and headings, Inter for the interface, Azeret Mono for measurements; tabular numerals everywhere a number is measured.
- The cube mark shown as delivered: a transparent PNG, never filtered, recoloured or backed.
- Dashed red edge means destruction and nothing else.

## Brand

- **Name:** MirbadLLM. Every user-visible mention of the product says MirbadLLM; command-line and package spellings use `mirbad-llm`. Configuration keys (`MirbadLLM__*`), environment variables, container, network, service and file names use the technical `mirbadllm` spelling; installs made under an earlier name keep theirs so they carry on (docs/setup.md, "Upgrading from a pre-rename install").
- **Mark:** `wwwroot/brand/mirbadllm-mark.png`, the isometric cube "M" (graphite and steel blocks interlocking with amber ones), cut from the delivered logo (`docs/brand/mirbadllm-mark-source.png`, on black), transparent. `favicon.png` (64px) is the same mark on a transparent square; `apple-touch-icon.png` (180px) keeps the delivered black background because iOS fills transparency with black anyway. The full lockup as delivered is `docs/brand/mirbadllm-logo-source.png`; its graphite "Mirbad" only reads on light backgrounds, so the UI sets the wordmark as live text instead.
- **Lockup:** the mark beside the wordmark set live in Plus Jakarta Sans 800, "Mirbad" in `text` and "LLM" in `accent`, so one markup serves both themes. It appears in the status strip, the rail drawer and the sign-in page.
- **Never** apply a CSS filter, recolour, silhouette, plate or tile to the mark. A vector (SVG) master of the mark would replace the PNG once one is drawn.

## Colors

A neutral paper (or graphite) ground carrying one brand amber and three state colours, each reserved for one meaning. Values in the frontmatter are normative: unsuffixed tokens are the dark theme, `-light` the light theme.

### Primary
- **Brand Amber** (`accent`, `accent-hover`, `accent-text`, `accent-subtle`, `focus`): the only action and selection colour. Fills the primary button, the current rail item, checked inputs (with a graphite tick) and range thumbs; `accent-text` draws links and accent text; `focus` is the single 2px keyboard ring. Amber (#f2a900) is the same in both themes and always carries graphite ink (#1e2226 light, #15181b dark), and hover lightens to #ffb72b. Amber is too light for text on paper, so in light `accent-text` is a deep amber (#8a5a00) and `focus` a mid amber (#a87400).

### Secondary
- **Amber as data** (`series-1`): data series one (sparklines, chart lines, the "MirbadLLM models" segment of the headroom bar, bar fills) and identifiers such as the model id beside its served name. Deep amber (#b07a00) in light so a line keeps 3:1 on the panel.
- **Dark amber** (`series-2`): data series two, always drawn dashed so it never relies on hue.
- **Categorical slots** (`cat-1` … `cat-8`): per-model series colours — amber, teal, magenta, violet, sand, slate, olive, indigo. They avoid every hue that carries a state (green, orange, red, and anything that reads as red).

### Tertiary (state)
- **Ready Green** (`ready`, `ready-subtle`): Ready, Online, a check that passed, a better benchmark delta.
- **Attention Orange** (`busy`, `busy-subtle`): Starting, Stopping, Degraded, Draining, a non-zero attention count, warnings. Orange, not amber, so a warning never reads as the action colour.
- **Destructive Red** (`failed`, `failed-subtle`): Failed, Incompatible, Revoked, validation errors, and destruction. `danger-fill` / `on-danger` is the confirm-in-dialog fill only.
- **Idle Steel** (`idle`, `idle-subtle`): Stopped, Offline, Pending approval.

### Neutral
- **Ground** (`page`): the page and rail background (#f7f7f5 / #15181b).
- **Panel** (`panel`): panels, cards, tables, inputs, dialogs.
- **Raised** (`elevated`): the active rail item, the tour card, the one-time tip.
- **Well** (`sunken`): logs and code, progress and bar tracks, segmented-control tracks, empty states, disabled inputs.
- **Hover Wash** (`hover`): row, nav and quiet-button hover.
- **Seam** (`border`): every panel edge, table rule and divider.
- **Control Line** (`border-control`): the stronger edge on anything operable, at least 3:1 against the panel.
- **Text / Muted Text** (`text`, `text-muted`): primary and secondary copy; muted passes 4.5:1 on ground, panel and raised in both themes.
- **Series steels** (`series-other`, `series-reserve`): "other processes" and the memory reserve; the reserve is drawn as a hatch over the free well.

### Named Rules
**The One Job Rule.** Amber means action, selection, focus and data; it never marks state. Green, orange and red never mark action or selection.

**The Never Colour Alone Rule.** Every state is an icon shape plus a text label in the state's colour (the StateBadge); series two is dashed; the reserve is hatched; "suggested" tags are dotted plus the word.

**The Contrast Floor Rule.** Every text/ground pair passes WCAG AA in both themes (muted text 5.4–5.8:1 light and 5.5–6.2:1 dark, state text on its own tint 4.6–7.2:1, graphite ink on amber 8.0–10.2:1, accent text 5.3–5.9:1 light, control line 3.5:1 on the panel). A new pair must be checked before it ships; `HardeningTests` checks the button, link and focus pairs.

## Typography

**Display Font:** Plus Jakarta Sans (the wordmark, page titles and headings; Inter and system-ui fallbacks)
**Interface Font:** Inter (with system-ui, -apple-system, Segoe UI, Roboto fallbacks)
**Measurement Font:** Azeret Mono (with ui-monospace, SF Mono, Cascadia Mono, Menlo, Consolas)

All three are self-hosted variable fonts (`wwwroot/fonts`, OFL; Inter and Plus Jakarta Sans as Google Fonts' Latin subsets) because the box is LAN-only; no font is fetched from the internet.

**Character:** Plus Jakarta Sans is the wordmark's geometric face, set bold for headings and extra-bold for the brand. Inter is neutral and highly legible at console sizes. Azeret Mono is wide and precise, so measurements read as instrument readouts and never as decoration.

### Hierarchy
- **Display** (Plus Jakarta Sans 500, 24px, 1.2, -0.01em): page titles and the model workspace's served name.
- **Headline** (Plus Jakarta Sans 600, 17px, 1.35): form-section titles, card and node-panel titles, empty-state and tour titles.
- **Title** (Plus Jakarta Sans 600, 14px, 1.4): sub-headings inside panels.
- **Body** (Inter 400, 14px, 1.5): all prose; subtitles and notes cap at 62–78ch.
- **Body small** (Inter 400, 12px): hints, tile labels and details, legends, notes. Operating tables use Inter 13px for served names and prose; identifiers and measured values stay in Azeret Mono.
- **Stat** (Azeret Mono 600, 20px, tabular): stat-tile values; the unit follows in 12px muted.
- **Data** (Azeret Mono, 14px, tabular): every measurement and identifier (`.num`), state badges, the status-strip readouts, logs and code.
- **Control** (Inter 500, 13px): buttons, rail links, tabs, segmented options.
- **Label** (Inter 500, 12px, tracked, uppercase): panel and section headers ("HEADROOM", "NEEDS ATTENTION").
- **Label small** (Inter 500, 11px, tracked, uppercase): rail group titles and table column heads.
- **V2 measured hierarchy:** node-card identities use 15px; compact table rates use 16px; Overview vitals and the node-detail identity use 26px; the Overview's generation lead (in the Signal panel) uses 38px. These steps distinguish a fleet total, page identity and row reading without enlarging prose. Touch inputs also use 16px in Inter to avoid browser zoom.
- **Live wordmark:** Plus Jakarta Sans 800 at 20px in the strip and 32px on sign-in, -0.02em, "LLM" in amber. These sizes are reserved for the brand.

### Named Rules
**The Measured Mono Rule.** Every number that is a measurement is Azeret Mono with tabular numerals; a missing value is "—", never 0, blank or an estimate. Mono is never used as a "technical" costume for navigation or buttons.

**The Caps Are Headers Rule.** Tracked caps are the panel's own heading, not a decoration above another heading.

## Layout

A persistent two-axis shell. A 48px sticky status strip spans the top (translucent ground with an 8px blur and a bottom seam) holding the MirbadLLM logo, the live readouts (Ready x / y, Nodes online, Attention — a non-zero count becomes an amber linked box), alert chips, and tour, shortcuts, theme and logout icon buttons. A 240px left rail below it orders navigation by how often each page is used: **Overview** first with no group title (it carries the live signal: the attention count, or the fleet's tok/s while nothing needs attention), then **Run** (Models, Playground, Access — start a model, try it, connect a client), **Fleet** (Nodes, with every node nested under it as a live tree), and **Admin** (Backup & restore, Releases). A soft footer card on the well holds the version link and the shortcuts key. The rail sits on the panel tone with a right seam. Content sits in `main` with 16px/24px padding and a 1440px max page width.

Pages open with a compact page header (title and 12px muted subtitle on one row, actions aligned right, wrapping). On phones the header text takes the full width and actions follow. Content insets are 16px vertically and 24px horizontally on desktop; sections and page columns are 16px apart. Headers with a back link retain their stacked workspace identity. A section head carries the caps label and an optional muted note or link. A model is a workspace: back link, served name + amber id + state badge, actions in place, node/runtime/budget chips, then line tabs (Overview, Settings, Logs, Benchmark, Tune). Forms are two-column sections: intro on the left, fields on the right.

Spacing is a 4/8 grid (4, 8, 12, 16, 24, 32, 48). Grids use explicit column counts or `auto-fill` minimums; tables stack per container width using container queries, not the viewport.

Responsive: below 960px the rail becomes a 300px drawer over an overlay; below 640px a four-item bottom bar (Overview, Models, Playground, Menu) replaces the logo in the top strip, alert chips (set in Inter so they fit) wrap to their own row, and the GPU readout drops. Under coarse pointers or phone width every control rises to 44px and inputs to 16px text.

## Elevation & Depth

Mostly flat. Depth comes from tonal steps — ground, panel, raised, well — with soft resting shadows in light and one-pixel seams in dark, following The Soft Panel Rule.

### Shadow Vocabulary
- **Floating layer** (`0 8px 24px rgb(0 0 0 / 0.25)`): popovers and row menus, only while open.
- **Drawer** (`0 8px 32px rgb(0 0 0 / 0.25)`): the rail as a drawer on tablet and phone.
- **Dialog** (`0 16px 48px rgb(0 0 0 / 0.28)`): modal dialogs over the overlay.
- **Tour card** (`0 12px 32px -8px rgb(0 0 0 / 0.45)`) and **tour spotlight** (`0 0 0 1.5px accent-text, 0 0 0 100vmax overlay`).
- **Soft elevation** (`--ss-shadow-1/2/3`, a light and a dark set in `app.css`): `shadow-1` for a resting panel, `shadow-2` for a card under the pointer or pressed, `shadow-3` for a side panel that floats over the page (the model side panel on Models).

### Named Rules
**The Soft Panel Rule.** Elevation is declared once. In the light theme a panel or card in the page flow carries `shadow-1` and no seam (a seam under a wide shadow is a ghost card); a card that responds to hover or press lifts to `shadow-2`. In the dark theme, where shadows vanish on graphite, the same surfaces carry the one-pixel seam and no resting shadow; only hover and pressed lift with `shadow-2`. A layer that floats above the page takes its own shadow from the vocabulary above, and only while it is open. Nothing else casts a shadow: no buttons, chips, inputs or table rows.

## Shapes

Soft-cornered: 12px (`rounded.lg`) for panels and dialogs, 8px for buttons, inputs, tabs and the tour card, 6px for state badges, alert chips, segmented options and code. Pills (999px) are reserved for tag chips, tab counts and scrollbar thumbs; circles for state dots and info buttons. Icons are 16px stroked line icons (1.75 stroke, round caps and joins; 20px in bars) from one icon set.

### Named Rules
**The Dashed Means Destruction Rule.** A dashed outline on a control or container means destruction and nothing else. In data marks, dashing is a stroke style for series two, never an outline.

## Components

### Buttons
- **Shape:** 8px radius, 32px tall (28px small, 44px touch), Inter 13px 500, 6px icon gap.
- **Primary:** amber fill (graphite ink in both themes); one per view, the main action ("Add model", "Save changes", "Start").
- **Default:** panel fill, control-line border, text colour; hover takes the hover wash.
- **Ghost:** transparent, muted text; hover lifts to text and the hover wash.
- **Destructive:** dashed red edge on a transparent ground at rest, red tint on hover; the solid red fill appears only on the confirm button inside a dialog.
- **Focus:** a 2px focus ring offset 2px, keyboard only; no glow.

### Chips
- **Alert chips:** 28px, 6px radius, Inter 12px 500 with an icon, failed or busy tint; they live in the status strip.
- **Model chips:** panel fill with a seam, key in muted, value in Azeret Mono; a linked chip (the node) in accent text.
- **Tag chips:** 20px pills on the well, muted 12px; filter toggles take the amber tint when pressed.

### Cards / Containers
- **Corner Style:** 12px. **Background:** panel on the ground; the well for empty states, logs and tracks. **Shadow:** `shadow-1` in light under The Soft Panel Rule. **Border:** the seam in dark only. **Padding:** 16px.

### Inputs / Fields
- **Style:** panel fill, control-line border, 8px radius, 32px tall, 14px Inter; placeholder in muted.
- **Focus:** the border turns amber; keyboard focus adds the 2px ring.
- **Error / Disabled:** red border and a 12px red message; disabled sinks to the well with muted text.
- **Segmented control:** a well track holding 6px-radius options; the chosen one is raised onto a panel chip with an inset control line.

### Navigation
- **Rail:** 38px items (44px on coarse pointers), Inter 13.5px 500, each icon in a 28px tile with an 8px radius. At rest the label is muted and the tile clear; hover takes the hover wash and the tile lifts to `elevated`; the active item takes the `accent-subtle` wash, text in text colour at 600, and its tile fills amber with the on-accent icon. Live counts sit at the right in Azeret Mono 11px: Overview's attention count in an amber pill, else the fleet tok/s after a green dot, Models `ready/total`, Nodes `reporting/total`. Under Nodes, a tree on a one-pixel guide line lists every node: state dot, id in Azeret Mono, a 40px memory bar (an empty seam when there is no reading); the node being viewed takes the amber wash. Group titles are 11px tracked caps.
- **Status strip:** the logo, then readout names in 11px tracked caps and values in tabular Azeret Mono.
- **Line tabs:** 40px Inter tabs on a seam; the selected tab goes semibold with a 2px amber underline.
- **Mobile:** drawer rail below 960px, 56px bottom bar below 640px with the active item in accent text.

### State Badge (signature)
A 24px, 6px-radius badge in Azeret Mono 12px with the state's own icon shape, label and tint. Dense model tables and fleet cards use the compact icon-and-word version without a tinted background or border. Starting spins its icon and shows a live elapsed timer.

### Memory Headroom (signature)
A segmented bar on the well: MirbadLLM models in amber, other processes in steel, the reserve as a hatch over the free well; a legend with tabular values and a note on what is measured versus budgeted.

### Node Card and Live Figures (Overview)
- **Node card:** a toggle button on the node rail: a 52px memory ring with the percentage inside, the node id in Azeret Mono, its state as icon + word, a GPU · °C · W line, and one 9px bead per placed model (filled in the model's colour while it runs, an empty ring when it does not) followed by "2 running · 1 off". Pressed takes the amber edge and `shadow-2`; it scopes the page to that node (pressing it again returns to All nodes). The scope rail is one row at any fleet size: it scrolls sideways with scroll snap and a hidden scrollbar, and phone-style page dots under it (one per screenful, the current one stretched with a transform, 44px targets on touch) follow the scroll and jump on click.
- **Ring gauge:** a ring on the well track for a 0–100 share (memory, KV cache). Unknown draws the bare track and reads "—"; the value is always printed inside or next to it.
- **Load pips:** 3×14px pips, amber running then orange waiting, at most 16, always followed by "running · waiting" in numbers.
- **Side panel:** a non-modal panel at the right of the page with `shadow-3`; Esc or its close button closes it and focus goes back to the row that opened it. Full width below 960px.
- **Command Deck (Overview):** header (window 5/15/60 min, All models, Add model), the node scope rail, the vitals band, the Signal panel (a WebGL stacked area of generation per model in the model colours over a GPU heat strip from the well to amber, orange above 80%; colours read from `--ss-*`, a static frame under reduced motion, a Canvas2D fallback, a visually hidden table of the same numbers) beside the Fleet today panel (four counters on the well: requests served, tokens today, crash-free streak, error budget), Service level on its own row (three ten-segment bars with a value and a word, never colour alone, side by side from 1024px, and streak chips), the leaderboard (rank per row), the inline readout (stamina bars for KV headroom and the latency budget), the node panels (headroom, GPU, temperature and power, peak, model disk, downloads; a scoped node's History folds under them) and three Next cards. The service-level formulas live in `ServiceLevels` (Orchestration).
- **Motion:** panels rise in once on load (520ms), chart lines draw in once, beads stay still (decorative pulse is banned; liveness is shown by data), and a figure that changed flashes the amber wash for 900ms. All of it is off under `prefers-reduced-motion`; only transform, opacity and colour animate.
- **Vitals band (Overview):** one panel split into six cells (Generation, Requests, TTFT p95, Error rate, Memory free, GPU): a tracked-caps label, an Azeret Mono 26px value (Generation in amber), a muted detail line and a 28px sparkline. Three per row below 1200px, two below 760px.
- **Grouped table (Models):** one table whose body is split by group rows (Running · n, then Stopped · n, foldable); a row opens the side panel; Start/Stop is a default button in the row, everything else sits in the row menu.
- **Node card (Nodes page):** headroom first (a 56px ring on unified memory, one bar per GPU on a discrete node), then the placed models as pills (filled dot while running), then alerts with an Open link; actions live in a row menu.
- **Save bar:** a sticky bar that rises in only when a settings form is dirty, with Discard changes and Save.
- **Wizard rail:** numbered steps in a vertical list (horizontal below 1200px); done steps take the ready tint, the current one the amber tile.
- **Release timeline:** a day column (date + year) beside a guide line; each merged PR is a card with its kind groups (Added green, Fixed teal, Changed amber squares).
- **Browser surfaces:** text selection is a 28% amber wash, scrollbars use the control line, caret and native accent controls use amber.

### Guided Tour and Tips
A nine-step tour anchored on `data-tour` elements: the page dims around the real element and one raised card explains it. One-time tips are raised notes in their own slot above the page.

### Danger Zone
The one place a destructive action lives: alone at the end of its page, a dashed red seam around its own row, a muted explanation and the destructive button.

## Do's and Don'ts

### Do:
- **Do** use the paper and graphite steps from the frontmatter as the only surfaces, in both themes.
- **Do** keep amber for action, selection, focus and data, and green, orange, red for state only.
- **Do** show state as icon + label + tint, and print "—" for any missing value.
- **Do** set measurements and identifiers in Azeret Mono with tabular numerals, headings and the wordmark in Plus Jakarta Sans, and everything else in Inter.
- **Do** show the cube mark exactly as delivered and say "MirbadLLM" wherever the product is named.
- **Do** check new colour pairs against WCAG AA in both themes before shipping.

### Don't:
- **Don't** filter, recolour, silhouette or back the logo with a plate or tile.
- **Don't** put a shadow on anything in the page flow except a panel or card under The Soft Panel Rule, pair a seam with a resting shadow, use `repeating-linear-gradient` stripes as a no-data fill, or a dashed outline on anything but destruction.
- **Don't** use mono for navigation, buttons or tabs.
- **Don't** convey state by colour alone, or animate layout properties.
- **Don't** fetch fonts or assets from the internet; Inter, Plus Jakarta Sans and Azeret Mono are self-hosted.
