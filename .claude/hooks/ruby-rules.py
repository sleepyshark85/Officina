#!/usr/bin/env python3
"""After an edit to a .rb file under ruby/: `# frozen_string_literal: true` as its first line, and no requirement IDs
in comments, as ruby/CLAUDE.md asks. A breach is reported to Claude to fix; the edit itself has already happened.
Fixture gems under test/fixtures/ are exempt."""
import json
import os
import re
import sys

FROZEN = "# frozen_string_literal: true"


def requirement_ids(root):
    """The requirement ID prefixes, such as TOOL or CTX, from REQUIREMENTS.md."""
    try:
        text = open(os.path.join(root, "REQUIREMENTS.md"), encoding="utf-8").read()
    except OSError:
        return None
    prefixes = sorted(set(re.findall(r"^\| ([A-Z]{2,5})-\d+ \|", text, re.M)))
    return re.compile(r"\b(?:" + "|".join(prefixes) + r")-\d+\b") if prefixes else None


def comments(text):
    """Each comment with its line number: a # outside a string literal to the end of the line, and =begin/=end blocks.
    A line-by-line reading of '…' and "…" literals, escapes included; other literals (%q, heredocs) are not read."""
    block = False
    for number, line in enumerate(text.splitlines(), 1):
        if block or line.startswith("=begin"):
            block = not line.startswith("=end")
            yield number, line
            continue
        quote, at = None, 0
        while at < len(line):
            char = line[at]
            if quote and char == "\\":
                at += 1
            elif quote:
                quote = None if char == quote else quote
            elif char in "'\"":
                quote = char
            elif char == "#":
                yield number, line[at:]
                break
            at += 1


def repository_root(path):
    """The nearest directory above the file that holds REQUIREMENTS.md, so a file in a worktree finds its own."""
    directory = os.path.dirname(os.path.abspath(path))
    while not os.path.exists(os.path.join(directory, "REQUIREMENTS.md")):
        parent = os.path.dirname(directory)
        if parent == directory:
            return None
        directory = parent
    return directory


def main():
    payload = json.load(sys.stdin)
    path = payload.get("tool_input", {}).get("file_path") or ""
    if not path.endswith(".rb") or not os.path.exists(path) or not (root := repository_root(path)):
        return
    relative = os.path.relpath(os.path.abspath(path), root).replace("\\", "/")
    if not relative.startswith("ruby/") or "/test/fixtures/" in relative:
        return
    text = open(path, encoding="utf-8").read()
    problems = []
    if text.splitlines()[:1] != [FROZEN]:
        problems.append(f"the first line is not '{FROZEN}'.")
    ids = requirement_ids(root)
    if ids:
        for number, comment in comments(text):
            if found := ids.search(comment):
                problems.append(f"line {number}: a code comment cites a requirement ID ({found.group(0)}); "
                                "test names carry the IDs instead.")
    if problems:
        print(f"{path} breaks ruby/CLAUDE.md:\n- " + "\n- ".join(problems), file=sys.stderr)
        sys.exit(2)


main()
