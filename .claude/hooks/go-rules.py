#!/usr/bin/env python3
"""After an edit to a .go file under go/: no requirement IDs in comments, and a doc.go in every package directory, as
go/CLAUDE.md asks. A breach is reported to Claude to fix; the edit itself has already happened. Fixture modules under
testdata/ are exempt."""
import json
import os
import re
import sys


def requirement_ids(root):
    """The requirement ID prefixes, such as TOOL or CTX, from REQUIREMENTS.md."""
    try:
        text = open(os.path.join(root, "REQUIREMENTS.md"), encoding="utf-8").read()
    except OSError:
        return None
    prefixes = sorted(set(re.findall(r"^\| ([A-Z]{2,5})-\d+ \|", text, re.M)))
    return re.compile(r"\b(?:" + "|".join(prefixes) + r")-\d+\b") if prefixes else None


def comments(text):
    """Each comment with its line number: // to the end of the line and /* */ blocks; a comment marker inside a string
    or rune literal (interpreted or raw) is not one."""
    at, line, size = 0, 1, len(text)
    while at < size:
        char = text[at]
        if text.startswith("//", at):
            end = text.find("\n", at)
            end = size if end < 0 else end
            yield line, text[at:end]
            at = end
        elif text.startswith("/*", at):
            end = text.find("*/", at + 2)
            end = size if end < 0 else end + 2
            yield line, text[at:end]
            line += text.count("\n", at, end)
            at = end
        elif char in "\"'`":
            end = at + 1
            while end < size and text[end] != char and (char == "`" or text[end] != "\n"):
                end += 2 if char != "`" and text[end] == "\\" else 1
            line += text.count("\n", at, end + 1)
            at = end + 1
        else:
            line += char == "\n"
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
    if not path.endswith(".go") or not os.path.exists(path) or not (root := repository_root(path)):
        return
    relative = os.path.relpath(os.path.abspath(path), root).replace("\\", "/")
    parts = relative.split("/")
    if parts[0] != "go" or "testdata" in parts:
        return
    problems = []
    directory = os.path.dirname(path)
    sources = [name for name in os.listdir(directory) if name.endswith(".go") and not name.endswith("_test.go")]
    if sources and "doc.go" not in sources:
        problems.append(f"the package in {os.path.dirname(relative)}/ has no doc.go; give it one holding the package comment.")
    ids = requirement_ids(root)
    if ids:
        text = open(path, encoding="utf-8").read()
        for number, comment in comments(text):
            if found := ids.search(comment):
                problems.append(f"line {number}: a code comment cites a requirement ID ({found.group(0)}); "
                                "test names carry the IDs instead.")
    if problems:
        print(f"{path} breaks go/CLAUDE.md:\n- " + "\n- ".join(problems), file=sys.stderr)
        sys.exit(2)


main()
