"""Generate the DESIGN.md diagrams (diagram-design skill, default skin)."""
import math, pathlib, sys

OUT = pathlib.Path(sys.argv[1])
OUT.mkdir(parents=True, exist_ok=True)

PAPER, INK, MUTED, SOFT = "#f5f5f5", "#2d3142", "#4f5d75", "#7a8399"
ACCENT, LINK = "#eb6c36", "#2e5aa8"
MONO, SANS = "'Geist Mono', monospace", "'Geist', sans-serif"

KINDS = {
    "normal":   dict(fill="#ffffff", stroke=INK, sw="1", dash=None),
    "store":    dict(fill="rgba(45,49,66,0.05)", stroke=MUTED, sw="0.8", dash=None),
    "external": dict(fill="rgba(45,49,66,0.03)", stroke="rgba(45,49,66,0.30)", sw="1", dash=None),
    "input":    dict(fill="rgba(79,93,117,0.10)", stroke=SOFT, sw="1", dash=None),
    "focal":    dict(fill="rgba(235,108,54,0.08)", stroke=ACCENT, sw="1.2", dash=None),
    "optional": dict(fill="rgba(45,49,66,0.02)", stroke="rgba(45,49,66,0.20)", sw="1", dash="4,3"),
}


def tw(text, size=8):
    """Mask width for a mono label, on the 4px grid."""
    w = len(text) * size * 0.66 + 6
    return int(math.ceil(w / 4) * 4)


class Svg:
    def __init__(self):
        self.zones, self.lines, self.labels, self.nodes, self.extra = [], [], [], [], []

    # connectors ---------------------------------------------------------
    def path(self, d, color=MUTED, dash=None, marker="arrow", sw="1.2"):
        da = f' stroke-dasharray="{dash}"' if dash else ""
        mk = f' marker-end="url(#{marker})"' if marker else ""
        self.lines.append(f'<path d="{d}" fill="none" stroke="{color}" stroke-width="{sw}"{da}{mk}/>')

    def label(self, cx, y_line, text, color=SOFT, where="above", x_side=None):
        w = tw(text)
        if where == "above":
            x, y = cx - w / 2, y_line - 20
        else:  # beside a vertical segment; cx = line x, y_line = centre y
            x, y = (x_side if x_side is not None else cx + 8), y_line - 6
        tx = x + w / 2
        self.labels.append(
            f'<rect x="{x:g}" y="{y:g}" width="{w}" height="12" rx="2" fill="{PAPER}"/>'
            f'<text x="{tx:g}" y="{y + 9:g}" fill="{color}" font-size="8" font-family="{MONO}" '
            f'text-anchor="middle" letter-spacing="0.06em">{text}</text>')

    # nodes --------------------------------------------------------------
    def node(self, x, y, w, h, name, sub=None, tag=None, kind="normal", badge=None, rx=6, size=12):
        k = KINDS[kind]
        da = f' stroke-dasharray="{k["dash"]}"' if k["dash"] else ""
        s = [f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" fill="{PAPER}"/>',
             f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" fill="{k["fill"]}" stroke="{k["stroke"]}" stroke-width="{k["sw"]}"{da}/>']
        tagc = ACCENT if kind == "focal" else INK
        if tag:
            tw_ = len(tag) * 5 + 12
            s.append(f'<rect x="{x + 8}" y="{y + 6}" width="{tw_}" height="12" rx="2" fill="none" stroke="{tagc}" stroke-opacity="0.4" stroke-width="0.8"/>'
                     f'<text x="{x + 8 + tw_ / 2:g}" y="{y + 15}" fill="{tagc}" font-size="7" font-family="{MONO}" text-anchor="middle" letter-spacing="0.08em">{tag}</text>')
        if badge:
            bw = len(badge) * 5 + 10
            s.append(f'<rect x="{x + w - 8 - bw}" y="{y + 6}" width="{bw}" height="12" rx="2" fill="none" stroke="{INK}" stroke-opacity="0.4" stroke-width="0.8"/>'
                     f'<text x="{x + w - 8 - bw / 2:g}" y="{y + 15}" fill="{INK}" font-size="8" font-family="{MONO}" text-anchor="middle">{badge}</text>')
        cx = x + w / 2
        cy = y + h / 2 + (6 if tag or badge else 0)
        ny = cy + (0 if sub else 4)
        s.append(f'<text x="{cx:g}" y="{ny:g}" fill="{INK}" font-size="{size}" font-weight="600" font-family="{SANS}" text-anchor="middle">{name}</text>')
        if sub:
            s.append(f'<text x="{cx:g}" y="{ny + 15:g}" fill="{MUTED}" font-size="9" font-family="{MONO}" text-anchor="middle">{sub}</text>')
        self.nodes.append("".join(s))

    def zone(self, x, y, w, h, text, boundary=False):
        if boundary:
            self.zones.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" fill="rgba(235,108,54,0.05)" stroke="{ACCENT}" stroke-opacity="0.5" stroke-width="1" stroke-dasharray="4,4"/>')
            col = ACCENT
        else:
            self.zones.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" fill="rgba(45,49,66,0.02)" stroke="rgba(45,49,66,0.10)" stroke-width="0.8"/>')
            col = "rgba(45,49,66,0.55)"
        lw = tw(text, 7)
        self.zones.append(f'<rect x="{x + 12}" y="{y + 4}" width="{lw}" height="12" rx="2" fill="{PAPER}"/>'
                          f'<text x="{x + 12 + lw / 2:g}" y="{y + 13}" fill="{col}" font-size="7" font-family="{MONO}" text-anchor="middle" letter-spacing="0.14em">{text}</text>')

    def legend(self, y, items, width=960):
        s = [f'<line x1="32" y1="{y - 8}" x2="{width - 32}" y2="{y - 8}" stroke="rgba(45,49,66,0.10)" stroke-width="0.8"/>',
             f'<text x="32" y="{y + 12}" fill="{MUTED}" font-size="8" font-family="{MONO}" letter-spacing="0.14em">LEGEND</text>']
        x = 112
        for kind, text in items:
            if kind in KINDS:
                k = KINDS[kind]
                da = f' stroke-dasharray="{k["dash"]}"' if k["dash"] else ""
                s.append(f'<rect x="{x}" y="{y + 3}" width="20" height="12" rx="3" fill="{k["fill"]}" stroke="{k["stroke"]}" stroke-width="{k["sw"]}"{da}/>')
            elif kind == "boundary":
                s.append(f'<rect x="{x}" y="{y + 3}" width="20" height="12" rx="3" fill="rgba(235,108,54,0.05)" stroke="{ACCENT}" stroke-opacity="0.5" stroke-dasharray="3,2"/>')
            else:  # line kinds: ("line", color, dash, marker)
                _, color, dash, marker = kind
                da = f' stroke-dasharray="{dash}"' if dash else ""
                s.append(f'<line x1="{x}" y1="{y + 9}" x2="{x + 20}" y2="{y + 9}" stroke="{color}" stroke-width="1.2"{da} marker-end="url(#{marker})"/>')
            s.append(f'<text x="{x + 28}" y="{y + 12}" fill="{MUTED}" font-size="9" font-family="{SANS}">{text}</text>')
            x += 28 + len(text) * 5.4 + 28
        self.extra.append("".join(s))

    def render(self, slug, title, desc, w, h):
        body = "\n".join(self.zones + self.lines + self.labels + self.nodes + self.extra)
        return f'''<svg viewBox="0 0 {w} {h}" xmlns="http://www.w3.org/2000/svg" role="img" aria-labelledby="{slug}-title {slug}-desc">
<title id="{slug}-title">{title}</title>
<desc id="{slug}-desc">{desc}</desc>
<defs>
<marker id="arrow" markerWidth="8" markerHeight="6" refX="7" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="{MUTED}"/></marker>
<marker id="arrow-accent" markerWidth="8" markerHeight="6" refX="7" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="{ACCENT}"/></marker>
<marker id="arrow-link" markerWidth="8" markerHeight="6" refX="7" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="{LINK}"/></marker>
<marker id="arrow-open" markerWidth="8" markerHeight="6" refX="7" refY="3" orient="auto"><polyline points="0 0, 8 3, 0 6" fill="none" stroke="{MUTED}" stroke-width="1.2"/></marker>
</defs>
<rect width="100%" height="100%" fill="{PAPER}"/>
{body}
</svg>'''


def page(slug, eyebrow, title, svg, caption):
    html = f'''<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>{title}</title>
<link href="https://fonts.googleapis.com/css2?family=Instrument+Serif:ital@0;1&family=Geist:wght@400;500;600&family=Geist+Mono:wght@400;500;600&family=Noto+Sans+KR:wght@400;500;600&family=Noto+Serif+KR:wght@400&family=Noto+Sans+TC:wght@400;500;600&family=Noto+Serif+TC:wght@400&display=swap" rel="stylesheet">
<style>
*, *::before, *::after {{ box-sizing: border-box; margin: 0; padding: 0; }}
:root {{ --color-paper: #f5f5f5; --color-ink: #2d3142; --color-muted: #4f5d75;
  --font-sans: 'Geist', system-ui, sans-serif; --font-serif: 'Instrument Serif', serif; --font-mono: 'Geist Mono', ui-monospace, monospace; }}
body {{ font-family: var(--font-sans); background: var(--color-paper); color: var(--color-ink);
  min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 3rem 2rem; }}
.frame {{ max-width: 1200px; width: 100%; }}
.eyebrow {{ font-family: var(--font-mono); font-size: 0.66rem; font-weight: 500; letter-spacing: 0.18em;
  text-transform: uppercase; color: var(--color-muted); margin-bottom: 0.5rem; }}
h1 {{ font-family: var(--font-serif); font-size: clamp(1.5rem, 2.4vw + 0.75rem, 2rem); font-weight: 400;
  letter-spacing: -0.02em; line-height: 1.15; margin-bottom: 1.5rem; }}
.caption {{ font-size: 0.85rem; color: var(--color-muted); margin-top: 1rem; max-width: 60rem; line-height: 1.5; }}
svg {{ width: 100%; min-width: 900px; display: block; }}
</style>
</head>
<body>
<div class="frame">
<p class="eyebrow">{eyebrow} · Officina design</p>
<h1>{title}</h1>
{svg}
<p class="caption">{caption}</p>
</div>
</body>
</html>
'''
    (OUT / f"{slug}.html").write_text(html)


def elbow_vh(x1, y1, ymid, x2, y2, r=8):
    """Down from (x1,y1) to ymid, across to x2, down to (x2,y2)."""
    sx = 1 if x2 > x1 else -1
    return (f"M {x1},{y1} V {ymid - r} Q {x1},{ymid} {x1 + sx * r},{ymid} "
            f"H {x2 - sx * r} Q {x2},{ymid} {x2},{ymid + r} V {y2}")


# ---------------------------------------------------------------- 1. solution layout
def solution():
    s = Svg()
    W, H = 960, 472
    s.zone(752, 168, 192, 224, "SDK BOUNDARY", boundary=True)
    # Cli -> capabilities, storage, mcp, claude
    s.path(elbow_vh(424, 136, 160, 296, 200))
    s.path("M 480,136 V 200")
    s.path(elbow_vh(512, 136, 176, 664, 200))
    s.path(elbow_vh(536, 136, 152, 848, 200))
    # rank 1 -> core
    s.path(elbow_vh(296, 256, 296, 424, 320))
    s.path("M 464,256 V 320")
    s.path(elbow_vh(664, 256, 296, 496, 320))
    s.path(elbow_vh(812, 256, 308, 536, 320))
    # testing -> core, claude -> sdk
    s.path("M 112,136 V 340 Q 112,348 120,348 H 400")
    s.path("M 884,256 V 320")
    s.node(32, 80, 160, 56, "Testing", "scripted models", "LIB", badge="0 IN")
    s.node(400, 80, 160, 56, "Cli", "the sof command", "APP", badge="0 IN")
    s.node(216, 200, 160, 56, "Capabilities", "4 projects", "LIB", badge="1 IN")
    s.node(400, 200, 160, 56, "Storage.Sqlite", "default storage", "LIB", badge="1 IN")
    s.node(584, 200, 160, 56, "Mcp", "own MCP client", "LIB", badge="1 IN")
    s.node(768, 200, 160, 56, "Providers.Claude", "only SDK user", "LIB", badge="1 IN")
    s.node(400, 320, 160, 56, "Core", "BCL only", "CORE", kind="store", badge="5 IN")
    s.node(768, 320, 160, 56, "Anthropic SDK", "NuGet: Anthropic", "EXT", kind="external", badge="1 IN")
    s.legend(424, [("normal", "Project"), ("store", "Leaf project"), ("external", "External package"), ("boundary", "SDK confined here (TEST-32)")])
    svg = s.render("solution-layout", "Solution layout",
                   "Dependency graph of the Officina .NET projects: every project depends on Sleepyshark.Officina.Core, and only Sleepyshark.Officina.Providers.Claude references the Anthropic SDK.", W, H)
    page("solution-layout", "Dependency graph", "Solution layout", svg,
         "Every project is Sleepyshark.Officina.&lt;name&gt;. Capabilities = Sleepyshark.Officina.Team, Sleepyshark.Officina.Workspace, Sleepyshark.Officina.Sandbox and Sleepyshark.Officina.Capabilities; Team and Capabilities are empty, as those capabilities live in Core. "
         "The CLI also references Sleepyshark.Officina.Core and Sleepyshark.Officina.Testing (for config dry-run) directly; neither is drawn. "
         "The SDK's transitive Microsoft.Extensions.AI.Abstractions never leaves the boundary.")


# ---------------------------------------------------------------- 2. runtime
def runtime():
    s = Svg()
    W, H = 960, 472
    y1, y2 = 100, 260
    # row A arrows
    s.path("M 152,128 H 200"); s.label(176, 128, "WORK")
    s.path("M 344,128 H 392"); s.label(368, 128, "TURN")
    s.path("M 552,128 H 600"); s.label(576, 128, "CALL")
    s.path("M 744,128 H 792", color=LINK, marker="arrow-link"); s.label(768, 128, "HTTPS", color=LINK)
    # turn -> tools, turn -> events
    s.path("M 440,156 V 260"); s.label(440, 208, "TOOLS", where="beside")
    s.path(elbow_vh(512, 156, 208, 656, 260)); s.label(656, 236, "EVENTS", where="beside")
    # tools -> run record, events -> storage, run record -> storage
    s.path("M 392,288 H 344"); s.label(368, 288, "RECORD")
    s.path("M 744,288 H 792"); s.label(768, 288, "APPEND")
    s.path("M 272,316 V 372 Q 272,380 280,380 H 848 Q 856,380 856,372 V 316"); s.label(560, 380, "COMMIT")
    s.node(32, y1, 120, 56, "Host", "CLI", "IN", kind="input")
    s.node(200, y1, 144, 56, "Agent actors", "1 turn at a time", "ACTOR")
    s.node(392, y1, 160, 56, "Turn engine", "the one primitive", "CORE", kind="focal")
    s.node(600, y1, 144, 56, "Model gateway", "rate limits · fallback", "SVC")
    s.node(792, y1, 128, 56, "Provider", "Claude API", "EXT", kind="external")
    s.node(200, y2, 144, 56, "Run record", "optimistic revisions", "SVC")
    s.node(392, y2, 160, 56, "Tool pipeline", "gates · audit · run", "SVC")
    s.node(600, y2, 144, 56, "Event bus", "ordered per agent", "SVC")
    s.node(792, y2, 128, 56, "Storage", "SQLite · WAL", "DB", kind="store")
    s.legend(424, [("focal", "Primitive"), ("normal", "Component"), ("store", "Store"), ("external", "External"), (("line", LINK, None, "arrow-link"), "API call")])
    svg = s.render("runtime", "Runtime", "Architecture of one run: agent actors hand turns to the turn engine, which calls the model through a shared gateway and runs tools through one pipeline, while the run record, by optimistic revisions, and the event bus write to storage.", W, H)
    page("runtime", "Architecture", "Runtime", svg,
         "The turn engine builds each model request itself (context builder). Patterns, including the team, only call the turn engine. "
         "Live consumers read the event bus through bounded queues and catch up from storage when they fall behind.")


# ---------------------------------------------------------------- 3. model request
def request():
    s = Svg()
    W, H = 960, 484
    X, LW = 112, 680
    rows = [("TOOLS", "Tool definitions", "sorted by name · per model slot", "normal"),
            ("SYSTEM", "Instructions and policies", "definition level only", "normal"),
            ("SYSTEM", "Project memory", "changes reach new conversations", "normal"),
            ("MESSAGES", "History", "append-only · never edited", "focal"),
            ("LAST", "Volatile context", "facts · knowledge · findings · task", "normal")]
    top, lh = 80, 60
    parts = []
    for i, (idx, name, sub, kind) in enumerate(rows):
        y = top + i * lh
        if kind == "focal":
            parts.append(f'<rect x="{X}" y="{y}" width="{LW}" height="{lh}" fill="rgba(235,108,54,0.08)" stroke="{ACCENT}" stroke-width="1.2"/>')
        else:
            fill = "#ffffff" if i % 2 == 0 else "#ececec"
            parts.append(f'<rect x="{X}" y="{y}" width="{LW}" height="{lh}" fill="{fill}" stroke="rgba(45,49,66,0.12)" stroke-width="1"/>')
        parts.append(f'<text x="{X + 16}" y="{y + 34}" fill="{ACCENT if kind == "focal" else MUTED}" font-size="8" font-family="{MONO}" letter-spacing="0.14em">{idx}</text>'
                     f'<text x="{X + 128}" y="{y + 35}" fill="{INK}" font-size="15" font-weight="600" font-family="{SANS}">{name}</text>'
                     f'<text x="{X + LW - 16}" y="{y + 34}" fill="{MUTED}" font-size="10" font-family="{MONO}" text-anchor="end">{sub}</text>')
    parts.append(f'<rect x="{X}" y="{top}" width="{LW}" height="{lh * 5}" fill="none" stroke="{MUTED}" stroke-width="1"/>')
    # cache boundaries on the right margin
    for n, (y, ttl) in enumerate([(top + 2 * lh, "1h"), (top + 3 * lh, "1h"), (top + 4 * lh, "5m")], 1):
        col = ACCENT if n == 3 else MUTED
        parts.append(f'<line x1="{X + LW}" y1="{y}" x2="{X + LW + 20}" y2="{y}" stroke="{col}" stroke-width="1.2"/>'
                     f'<circle cx="{X + LW + 28}" cy="{y}" r="8" fill="{PAPER}" stroke="{col}" stroke-width="1"/>'
                     f'<text x="{X + LW + 28}" y="{y + 3}" fill="{col}" font-size="8" font-weight="600" font-family="{MONO}" text-anchor="middle">{n}</text>'
                     f'<text x="{X + LW + 44}" y="{y + 3}" fill="{col}" font-size="8" font-family="{MONO}" letter-spacing="0.06em">CACHE · {ttl}</text>')
    # order indicator
    parts.append(f'<text x="64" y="{top + 8}" fill="{MUTED}" font-size="8" font-family="{MONO}" text-anchor="middle" letter-spacing="0.12em">FIRST</text>'
                 f'<line x1="64" y1="{top + 20}" x2="64" y2="{top + 5 * lh - 20}" stroke="{MUTED}" stroke-width="1" marker-end="url(#arrow)"/>'
                 f'<text x="64" y="{top + 5 * lh}" fill="{MUTED}" font-size="8" font-family="{MONO}" text-anchor="middle" letter-spacing="0.12em">LAST</text>')
    s.extra.extend(parts)
    s.legend(436, [("focal", "The rule that keeps caching and reasoning valid"), (("line", MUTED, None, "arrow"), "Order sent to the model")])
    svg = s.render("model-request", "Model request and caching",
                   "Layer stack of one model request, from tool definitions and instructions through project memory and append-only history to the volatile context, with three cache boundaries.", W, H)
    page("model-request", "Layer stack", "Model request and caching", svg,
         "Every call only appends to the previous one. On Claude the volatile context is a turn-scoped system message, "
         "or, where that is unavailable, an appended text block that later calls in the turn extend with changes only (CTX-10, CTX-11).")


# ---------------------------------------------------------------- sequence helpers
class Seq(Svg):
    def __init__(self, actors, top=40, bottom=560):
        super().__init__()
        self.xs = {}
        for name, (cx, sub, kind) in actors.items():
            self.xs[name] = cx
            self.lines.append(f'<line x1="{cx}" y1="{top + 48}" x2="{cx}" y2="{bottom}" stroke="rgba(45,49,66,0.20)" stroke-width="1" stroke-dasharray="3,3"/>')
            self.node(cx - 72, top, 144, 48, name, sub, kind=kind)

    def msg(self, a, b, y, text, kind="call", lx=None):
        x1, x2 = self.xs[a], self.xs[b]
        d = 1 if x2 > x1 else -1
        color, dash, marker = {"call": (MUTED, None, "arrow"), "return": (MUTED, "5,4", "arrow"),
                               "async": (MUTED, "5,4", "arrow-open"), "headline": (ACCENT, None, "arrow-accent")}[kind]
        self.path(f"M {x1 + 4 * d},{y} H {x2 - 4 * d}", color=color, dash=dash, marker=marker)
        self.label(lx if lx is not None else (x1 + x2) / 2, y, text, color=ACCENT if kind == "headline" else SOFT)

    def self_msg(self, a, y, text):
        x = self.xs[a]
        self.path(f"M {x + 4},{y} H {x + 32} Q {x + 40},{y} {x + 40},{y + 8} V {y + 16} Q {x + 40},{y + 24} {x + 32},{y + 24} H {x + 6}")
        w = tw(text)
        self.labels.append(f'<rect x="{x + 48}" y="{y + 6}" width="{w}" height="12" rx="2" fill="{PAPER}"/>'
                           f'<text x="{x + 48 + w / 2:g}" y="{y + 15}" fill="{SOFT}" font-size="8" font-family="{MONO}" text-anchor="middle" letter-spacing="0.06em">{text}</text>')

    def frame(self, x, y, w, h, op, guard):
        self.zones.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="4" fill="rgba(45,49,66,0.02)" stroke="rgba(45,49,66,0.22)" stroke-width="1"/>')
        self.labels.append(f'<rect x="{x}" y="{y}" width="40" height="16" rx="2" fill="{PAPER}" stroke="rgba(45,49,66,0.22)" stroke-width="1"/>'
                          f'<text x="{x + 20}" y="{y + 12}" fill="{MUTED}" font-size="8" font-family="{MONO}" text-anchor="middle" letter-spacing="0.12em">{op}</text>'
                          f'<rect x="{x + 48}" y="{y + 3}" width="{tw(guard)}" height="12" rx="2" fill="{PAPER}"/>'
                          f'<text x="{x + 52}" y="{y + 12}" fill="{MUTED}" font-size="8" font-family="{MONO}" letter-spacing="0.04em">{guard}</text>')


# ---------------------------------------------------------------- 4. team
def team():
    W, H = 960, 656
    s = Seq({"Owner": (96, "human", "input"), "Lead": (288, "agent", "normal"),
             "Task board": (480, "and integration queue", "store"),
             "Developer ×N": (672, "own working copy", "normal"), "Reviewer": (864, "never the author", "normal")},
            bottom=588)
    s.msg("Owner", "Lead", 124, "GOAL")
    s.msg("Lead", "Task board", 156, "PLAN → TASKS")
    s.msg("Task board", "Owner", 188, "APPROVE PLAN?", lx=384)
    s.msg("Owner", "Task board", 220, "APPROVED", kind="return", lx=192)
    s.frame(404, 244, 536, 264, "LOOP", "[per task, up to 4 in parallel]")
    s.msg("Task board", "Developer ×N", 292, "READY TASK")
    s.self_msg("Developer ×N", 308, "EDIT · BUILD · TEST")
    s.msg("Developer ×N", "Task board", 364, "SUBMIT", kind="call")
    s.msg("Task board", "Reviewer", 396, "REVIEW", lx=768)
    s.msg("Reviewer", "Task board", 428, "VERDICT", kind="return", lx=576)
    s.self_msg("Task board", 452, "REBASE + CHECKS")
    s.msg("Task board", "Lead", 540, "RESULT", kind="return", lx=384)
    s.msg("Lead", "Owner", 572, "RUN REPORT", kind="headline")
    s.legend(612, [(("line", MUTED, None, "arrow"), "Call"), (("line", MUTED, "5,4", "arrow"), "Reply"), (("line", ACCENT, None, "arrow-accent"), "Outcome")])
    svg = s.render("team-run", "Team run", "Sequence of a team run: the owner gives a goal, the lead plans tasks, the owner approves the plan, developers work tasks in parallel with review and integration, and the lead reports.", W, H)
    page("team-run", "Sequence", "Team run", svg,
         "A task reaches done only when its verification checks pass, its review passes, and it integrates onto the "
         "current baseline with the baseline checks still passing. A conflict goes back to the author or the lead.")


# ---------------------------------------------------------------- 5. task states
def tasks():
    s = Svg()
    W, H = 960, 520
    y, h = 168, 64
    # main row
    s.path("M 38,200 H 64")
    s.path("M 176,200 H 232"); s.label(204, 200, "DEPS DONE")
    s.path("M 344,200 H 400"); s.label(372, 200, "CLAIM")
    s.path("M 528,200 H 584"); s.label(556, 200, "VERIFIED")
    s.path("M 696,200 H 752", color=ACCENT, marker="arrow-accent", sw="1.4"); s.label(724, 200, "INTEGRATE", color=ACCENT)
    s.path("M 864,200 H 900")
    # back edge: in review -> in progress
    s.path("M 640,168 V 136 Q 640,128 632,128 H 472 Q 464,128 464,136 V 168", dash="5,4"); s.label(552, 128, "CHANGES ASKED")
    # blocked
    s.path("M 432,232 V 320"); s.label(432, 276, "BLOCK", where="beside")
    s.path("M 400,352 H 320 Q 312,352 312,344 V 232"); s.label(356, 352, "UNBLOCK")
    # failed
    s.path("M 512,232 V 344 Q 512,352 520,352 H 584"); s.label(552, 352, "EXHAUSTED")
    s.path("M 640,384 V 408 Q 640,416 632,416 H 288 Q 280,416 280,408 V 232"); s.label(460, 416, "RETRY · SPLIT")
    # start / end dots
    s.nodes.append(f'<circle cx="32" cy="200" r="6" fill="{INK}"/>')
    s.nodes.append(f'<circle cx="908" cy="200" r="8" fill="none" stroke="{INK}" stroke-width="1"/><circle cx="908" cy="200" r="5" fill="{INK}"/>')
    s.node(64, y, 112, h, "Proposed", "by the lead", rx=8)
    s.node(232, y, 112, h, "Ready", "deps done", rx=8)
    s.node(400, y, 128, h, "In progress", "one agent", rx=8)
    s.node(584, y, 112, h, "In review", "not the author", rx=8)
    s.node(752, y, 112, h, "Done", "checks passed", kind="focal", rx=8)
    s.node(400, 320, 96, h, "Blocked", "waiting", rx=8)
    s.node(584, 320, 112, h, "Failed", "back to lead", rx=8)
    s.node(64, 320, 112, h, "Cancelled", "by the owner", kind="optional", rx=8)
    s.extra.append(f'<text x="120" y="408" fill="{MUTED}" font-size="8" font-family="{MONO}" text-anchor="middle" letter-spacing="0.04em">* → CANCELLED</text>')
    s.legend(468, [("focal", "Done only when checks pass"), ("optional", "Reachable from any open state"), (("line", MUTED, "5,4", "arrow"), "Rework")])
    svg = s.render("task-states", "Task states", "State machine of a task: proposed, ready, in progress, in review and done, with side states blocked, failed and cancelled.", W, H)
    page("task-states", "State machine", "Task states", svg,
         "The transitions are fixed in code (TASK-02). Failing the attempt limit (default 3) or the task budget sends the task back to the lead (TASK-09).")


# ---------------------------------------------------------------- 6. resume
def resume():
    W, H = 960, 488
    s = Seq({"Host": (120, "CLI", "input"), "Run": (360, "resumed run", "normal"),
             "Storage": (600, "SQLite", "store"), "Workspace": (840, "git worktrees", "normal")}, bottom=424)
    s.msg("Host", "Run", 124, "RESUME")
    s.msg("Run", "Storage", 156, "CHECKPOINT")
    s.msg("Storage", "Run", 188, "OFFSETS · SHAS", kind="return")
    s.msg("Run", "Workspace", 220, "RESET TO SHAS", lx=480)
    s.msg("Run", "Storage", 252, "OPEN INTENTS?")
    s.msg("Storage", "Run", 284, "NO OUTCOME YET", kind="return")
    s.msg("Run", "Host", 316, "FLAG FOR HUMAN", kind="async")
    s.self_msg("Run", 332, "REDO TURNS")
    s.msg("Run", "Host", 400, "RESUMED", kind="headline")
    s.legend(448, [(("line", MUTED, None, "arrow"), "Call"), (("line", MUTED, "5,4", "arrow"), "Reply"), (("line", MUTED, "5,4", "arrow-open"), "Notify"), (("line", ACCENT, None, "arrow-accent"), "Outcome")])
    svg = s.render("resume", "Crash and resume", "Sequence of resuming a run after a crash: load the last checkpoint, reset worktrees, flag unfinished write intents for a human, then redo interrupted turns.", W, H)
    page("resume", "Sequence", "Crash and resume", svg,
         "History is append-only, so a checkpoint is just message counts, record, task and memory revisions, and worktree commit SHAs. "
         "An irreversible call with an intent but no outcome is never re-run (TOOL-10, RUN-07).")


for f in (solution, runtime, request, team, tasks, resume):
    f()
print("written", sorted(p.name for p in OUT.iterdir()))
