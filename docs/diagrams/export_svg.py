"""Export each diagram page to a standalone SVG (diagram-design export procedure)."""
import re, pathlib, sys
FONTS = ("<style>@import url('https://fonts.googleapis.com/css2?family=Instrument+Serif:ital@0;1&amp;family=Geist:wght@400;500;600"
         "&amp;family=Geist+Mono:wght@400;500;600&amp;family=Noto+Sans+KR:wght@400;500;600&amp;family=Noto+Serif+KR:wght@400"
         "&amp;family=Noto+Sans+TC:wght@400;500;600&amp;family=Noto+Serif+TC:wght@400&amp;display=swap');</style>")
here = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".")
for f in sorted(here.glob("*.html")):
    svg = re.search(r"<svg\b.*?</svg>", f.read_text(), re.S).group(0)
    svg = svg.replace("<defs>", "<defs>\n" + FONTS, 1)
    svg = re.sub(r'(fill|stroke)="rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d*\.?\d+)\s*\)"',
                 lambda m: '{0}="#{1:02x}{2:02x}{3:02x}" {0}-opacity="{4}"'.format(m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4)), m.group(5)), svg)
    svg = re.sub(r'(fill|stroke)="transparent"', r'\1="none"', svg)
    f.with_suffix(".svg").write_text('<?xml version="1.0" encoding="UTF-8"?>\n' + svg + "\n")
    print(f.with_suffix(".svg").name)
