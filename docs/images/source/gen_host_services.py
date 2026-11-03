# Generates host-services.html in the same primitives as the other MirbadLLM architecture diagrams.
import sys, html

PAPER, WHITE, INK, MUTED, SOFT = "#f7f7f5", "#ffffff", "#1e2226", "#5f676e", "#7f8a92"
ACCENT, ACCENT_TINT, LINK = "#8a5a00", "rgba(242,169,0,0.16)", "#0f6863"
SANS = "'Inter', system-ui, sans-serif"
MONO = "'Azeret Mono', ui-monospace, monospace"
SERIF = "'Plus Jakarta Sans', 'Inter', system-ui, sans-serif"

STYLES = {  # fill, stroke, tag stroke, tag text
    "master": (WHITE, INK, "rgba(30,34,38,0.40)", INK),
    "state": ("rgba(30,34,38,0.05)", MUTED, "rgba(95,103,110,0.50)", MUTED),
    "ext": ("rgba(95,103,110,0.10)", SOFT, "rgba(127,138,146,0.40)", SOFT),
    "accent": (ACCENT_TINT, ACCENT, "rgba(138,90,0,0.50)", ACCENT),
}
out = []
e = lambda s: html.escape(s, quote=False)


def zone(x, y, w, h, label):
    tw = len(label) * 5.7 + 8
    out.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" fill="rgba(30,34,38,0.02)" stroke="rgba(30,34,38,0.16)" stroke-width="0.8"/>')
    out.append(f'<rect x="{x+12}" y="{y-6}" width="{tw:.0f}" height="12" rx="2" fill="{PAPER}"/>')
    out.append(f'<text x="{x+12+tw/2:.1f}" y="{y+3}" fill="{SOFT}" font-size="7.5" font-family="{MONO}" text-anchor="middle" letter-spacing="0.14em">{e(label)}</text>')


def arrow(d, kind="muted", dashed=False):
    stroke, marker, width = {"muted": (MUTED, "arrow", 1.2), "link": (LINK, "arrow-link", 1.2), "accent": (ACCENT, "arrow-accent", 1.4)}[kind]
    dash = ' stroke-dasharray="5,4"' if dashed else ""
    out.append(f'<path d="{d}" fill="none" stroke="{stroke}" stroke-width="{width}"{dash} marker-end="url(#{marker})"/>')


def label(cx, y, text, kind="muted"):
    color = {"muted": MUTED, "link": LINK, "accent": ACCENT}[kind]
    w = len(text) * 5.5 + 8
    out.append(f'<rect x="{cx-w/2:.1f}" y="{y}" width="{w:.0f}" height="12" rx="2" fill="{PAPER}"/>')
    out.append(f'<text x="{cx}" y="{y+9}" fill="{color}" font-size="8" font-family="{MONO}" text-anchor="middle" letter-spacing="0.06em">{e(text)}</text>')


def node(x, y, w, h, tag, name, subs, style="master"):
    fill, stroke, tag_stroke, tag_text = STYLES[style]
    tw = len(tag) * 5 + 10
    cx = x + w / 2
    out.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="6" fill="{PAPER}"/>')
    out.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="6" fill="{fill}" stroke="{stroke}" stroke-width="1"/>')
    out.append(f'<rect x="{x+8}" y="{y+6}" width="{tw}" height="12" rx="2" fill="transparent" stroke="{tag_stroke}" stroke-width="0.8"/>')
    out.append(f'<text x="{x+8+tw/2}" y="{y+15}" fill="{tag_text}" font-size="7" font-family="{MONO}" text-anchor="middle" letter-spacing="0.08em">{e(tag)}</text>')
    top = y + h / 2 - (len(subs) * 14) / 2 + 5
    out.append(f'<text x="{cx}" y="{top}" fill="{INK}" font-size="12" font-weight="600" font-family="{SANS}" text-anchor="middle">{e(name)}</text>')
    for i, s in enumerate(subs):
        out.append(f'<text x="{cx}" y="{top+16+i*14}" fill="{MUTED}" font-size="9" font-family="{MONO}" text-anchor="middle">{e(s)}</text>')


# Zones
zone(24, 112, 200, 128, "BROWSER")
zone(256, 112, 264, 392, "MASTER · MirbadLLM.Web")
zone(552, 112, 664, 392, "NODES · ONE AGENT PER MACHINE")

# Arrows first, then labels, then nodes (masks before the nodes drawn after them).
arrow("M 208,184 H 280", "link")
arrow("M 388,224 V 292")
arrow("M 496,170 H 576", "link")
arrow("M 496,188 H 532 Q 540,188 540,196 V 344 Q 540,352 548,352 H 576", "link")
arrow("M 496,206 H 520 Q 528,206 528,214 V 440 Q 528,448 536,448 H 576", "link")
arrow("M 768,168 H 824")
arrow("M 768,208 H 788 Q 796,208 796,216 V 240 Q 796,248 804,248 H 824", "accent")
label(244, 164, ":8080", "link")
label(536, 150, "MTLS :9443", "link")
label(796, 148, "DOCKER", "muted")

node(40, 152, 168, 64, "EXT", "Operator browser", ["Services page"], "ext")
node(280, 152, 216, 72, "CORE", "Host service manager",
     ["whitelists · confirmations", "session authorization"])
node(280, 292, 216, 64, "STATE", "host-services.json", ["whitelist · restart policies"], "state")
node(576, 152, 192, 72, "AGENT", "Node agent · Linux", ["mirbadllm-agent", "container + unit backends"])
node(824, 132, 368, 64, "ENGINE", "Docker engine", ["discover every container · control whitelisted ones"], "state")
node(824, 216, 368, 72, "ROOT", "mirbadllm-hostd",
     ["systemd · root · Unix socket only, no TCP", "deny list · unit whitelist · systemctl · nvidia-smi"], "accent")
node(576, 320, 616, 64, "AGENT", "Node agent · Windows PC", ["Windows services through the Service Control Manager (SCM)"])
node(576, 416, 616, 64, "AGENT", "Node agent · Apple Silicon Mac",
     ["launchd jobs (Ollama, LM Studio, ComfyUI) through mirbadllm-hostd (launchd)"])

out.append(f'<text x="576" y="536" fill="{MUTED}" font-size="12" font-style="italic" font-family="{SERIF}">Only hostd runs systemctl, as root, and only for whitelisted units the deny list allows.</text>')

# Legend
out.append(f'<line x1="24" y1="564" x2="1216" y2="564" stroke="rgba(30,34,38,0.10)" stroke-width="0.8"/>')
out.append(f'<text x="24" y="582" fill="{MUTED}" font-size="8" font-family="{MONO}" letter-spacing="0.18em">LEGEND</text>')
lx = 96
for style, text in [("master", "Component"), ("state", "State or engine"), ("ext", "Client"), ("accent", "Root boundary")]:
    fill, stroke, _, _ = STYLES[style]
    out.append(f'<rect x="{lx}" y="574" width="14" height="10" rx="2" fill="{fill}" stroke="{stroke}" stroke-width="1"/>')
    out.append(f'<text x="{lx+20}" y="582" fill="{MUTED}" font-size="9" font-family="{SANS}">{text}</text>')
    lx += 28 + len(text) * 5.2 + 24
for kind, marker, text in [(LINK, "arrow-link", "HTTP / mTLS call"), (ACCENT, "arrow-accent", "Unix socket to root"), (MUTED, "arrow", "Local call")]:
    out.append(f'<line x1="{lx:.0f}" y1="579" x2="{lx+24:.0f}" y2="579" stroke="{kind}" stroke-width="1.2" marker-end="url(#{marker})"/>')
    out.append(f'<text x="{lx+30:.0f}" y="582" fill="{MUTED}" font-size="9" font-family="{SANS}">{text}</text>')
    lx += 30 + len(text) * 5.2 + 24

svg = f'''<svg viewBox="0 48 1240 592" xmlns="http://www.w3.org/2000/svg" role="img" aria-labelledby="host-services-title host-services-desc">
<title id="host-services-title">Services page and host agents</title>
<desc id="host-services-desc">The master's host service manager keeps the whitelists and confirmations and calls each node agent over mTLS; on Linux the agent controls containers through Docker and units through the root mirbadllm-hostd, on Windows through the SCM and on a Mac through hostd under launchd.</desc>
<defs>
<marker id="arrow" markerWidth="8" markerHeight="6" refX="7" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="{MUTED}"/></marker>
<marker id="arrow-accent" markerWidth="8" markerHeight="6" refX="7" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="{ACCENT}"/></marker>
<marker id="arrow-link" markerWidth="8" markerHeight="6" refX="7" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="{LINK}"/></marker>
</defs>
<rect x="0" y="48" width="1240" height="592" fill="{PAPER}"/>
''' + "\n".join(out) + "\n</svg>"

shell = open(sys.argv[1], encoding="utf-8").read()  # any themed diagram page, used for its <head>
head = shell[: shell.index("<body>")].replace(shell[shell.index("<title>"): shell.index("</title>") + 8], "<title>Services page and host agents</title>")
open(sys.argv[2], "w", encoding="utf-8").write(
    head + f'<body>\n  <div class="frame">\n    <p class="eyebrow">Architecture · MirbadLLM</p>\n    <h1>Services page and host agents</h1>\n    {svg}\n  </div>\n</body>\n</html>\n')
