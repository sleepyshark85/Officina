#!/usr/bin/env python3
"""After an edit to a .cs file: one top-level type per file, named after it, and no requirement IDs in comments, as
CLAUDE.md asks. A breach is reported to Claude to fix; the edit itself has already happened."""
import json
import os
import re
import sys

DECLARATION = re.compile(
    r"^(?:public |internal |file )?(?:static |sealed |abstract |partial |readonly |ref )*"
    r"(?:class|record|struct|interface|enum)\b(?: (?:class|struct))?\s+([A-Za-z_]\w*)", re.M)


def requirement_ids(root):
    """The requirement ID prefixes, such as TOOL or CTX, from REQUIREMENTS.md."""
    try:
        text = open(os.path.join(root, "REQUIREMENTS.md"), encoding="utf-8").read()
    except OSError:
        return None
    prefixes = sorted(set(re.findall(r"^\| ([A-Z]{2,5})-\d+ \|", text, re.M)))
    return re.compile(r"\b(?:" + "|".join(prefixes) + r")-\d+\b") if prefixes else None


def comment_of(line):
    """The line's // comment, whole-line or trailing; a // inside a string literal is not one."""
    at = 0
    while (at := line.find("//", at)) >= 0:
        if line[:at].count('"') % 2 == 0:
            return line[at:]
        at += 2
    return None


def main():
    payload = json.load(sys.stdin)
    path = payload.get("tool_input", {}).get("file_path") or ""
    root = payload.get("cwd") or "."
    parts = path.replace("\\", "/").split("/")
    if not path.endswith(".cs") or "bin" in parts or "obj" in parts or "spikes" in parts or not os.path.exists(path):
        return
    text = open(path, encoding="utf-8").read()
    problems = []
    types = DECLARATION.findall(text)
    name = os.path.basename(path)
    if len(types) > 1:
        problems.append(f"{name} declares {len(types)} top-level types ({', '.join(types)}); give each its own file.")
    elif len(types) == 1 and name != types[0] + ".cs":
        problems.append(f"{name} declares {types[0]}; name the file {types[0]}.cs.")
    ids = requirement_ids(root)
    if ids:
        for number, line in enumerate(text.split("\n"), 1):
            if (comment := comment_of(line)) and (found := ids.search(comment)):
                problems.append(f"line {number}: a code comment cites a requirement ID ({found.group(0)}); "
                                "test names carry the IDs instead.")
    if problems:
        print(f"{path} breaks CLAUDE.md:\n- " + "\n- ".join(problems), file=sys.stderr)
        sys.exit(2)


main()
