"""Check that every requirement in REQUIREMENTS.md is closed by exactly one slice."""
import re, pathlib, sys
root = pathlib.Path(__file__).resolve().parents[2]
reqs = dict(re.findall(r"^\| ([A-Z]+-\d+) \| (MUST|SHOULD|MAY) \|", (root / "REQUIREMENTS.md").read_text(), re.M))
owner = {}
problems = []
for f in sorted(pathlib.Path(__file__).parent.glob("S*.md")):
    m = re.search(r"^\*\*Closes:\*\* (.*)$", f.read_text(), re.M)
    for rid in re.findall(r"[A-Z]+-\d+", m.group(1) if m else ""):
        if rid not in reqs: problems.append(f"{f.name}: {rid} is not a requirement")
        elif rid in owner: problems.append(f"{rid} is closed by both {owner[rid]} and {f.name}")
        else: owner[rid] = f.name
for rid, pri in reqs.items():
    if rid not in owner: problems.append(f"{rid} ({pri}) is not closed by any slice")
print("\n".join(problems) or f"OK: all {len(reqs)} requirements are closed by exactly one slice")
sys.exit(1 if problems else 0)
