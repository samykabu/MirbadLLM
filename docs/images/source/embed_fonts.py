# Replaces the exporter's remote font styles with the brand fonts, subset to each diagram's text and inlined
# as base64 WOFF2, so the SVG renders in the brand fonts inside an <img> (which never loads remote fonts).
# Usage: embed_fonts.py <fontsource node_modules dir> <in.svg> <out.svg>
import base64, html, io, re, sys
from fontTools import subset
from fontTools.ttLib import TTFont

nm, src, dst = sys.argv[1:4]
FONTS = [  # family, file, weight (range for variable fonts), style
    ("Inter", "@fontsource-variable/inter/files/inter-latin-wght-normal.woff2", "100 900", "normal"),
    # The diagrams use Plus Jakarta Sans only for italic callouts.
    ("Plus Jakarta Sans", "@fontsource-variable/plus-jakarta-sans/files/plus-jakarta-sans-latin-wght-italic.woff2", "200 800", "italic"),
    ("Azeret Mono", "@fontsource/azeret-mono/files/azeret-mono-latin-400-normal.woff2", "400", "normal"),
    ("Azeret Mono", "@fontsource/azeret-mono/files/azeret-mono-latin-600-normal.woff2", "600", "normal"),
]

svg = open(src, encoding="utf-8").read()
svg = re.sub(r'<style data-illustrate-export-[^>]*>.*?</style>', "", svg, flags=re.S)


def texts_for(family):
    out = []
    for m in re.finditer(r"<text([^>]*)>(.*?)</text>", svg, re.S):
        attrs, body = m.groups()
        fam = re.search(r'font-family="([^"]*)"', attrs)
        if fam and html.unescape(fam.group(1)).lstrip().startswith(f"'{family}'"):
            out.append(html.unescape(re.sub(r"<[^>]+>", "", body)))
    return "".join(out)


faces = []
for family, path, weight, style in FONTS:
    text = texts_for(family)
    if not text:
        continue
    font = TTFont(f"{nm}/{path}")
    opts = subset.Options(); opts.flavor = "woff2"; opts.layout_features = ["*"]
    sub = subset.Subsetter(opts); sub.populate(text=text + " "); sub.subset(font)
    buf = io.BytesIO(); font.flavor = "woff2"; font.save(buf)
    data = base64.b64encode(buf.getvalue()).decode()
    faces.append(f"@font-face{{font-family:'{family}';font-weight:{weight};font-style:{style};"
                 f"src:url(data:font/woff2;base64,{data}) format('woff2')}}")

style = "<style>" + "".join(faces) + "</style>"
svg = svg.replace("<defs>", "<defs>" + style, 1)
assert style in svg, "no <defs> to hold the fonts"
open(dst, "w", encoding="utf-8", newline="\n").write(svg)
print(f"{dst}: {len(svg.encode())} bytes, {len(faces)} faces")
