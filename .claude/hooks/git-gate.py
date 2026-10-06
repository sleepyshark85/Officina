#!/usr/bin/env python3
"""Before a Bash command: blocks pushing to main, committing on main and branches without an allowed prefix, as
CLAUDE.md asks; before a commit, shows the staged files so an unexpected one is caught."""
import json
import re
import shlex
import subprocess
import sys

PREFIXES = ("slice/", "docs/", "fix/", "refactor/", "chore/", "test/", "feature/")


def git(cwd, *args):
    run = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
    return run.stdout.strip() if run.returncode == 0 else ""


def commands(text):
    """Each simple command in the line, as words; operators and newlines separate them."""
    for part in re.split(r"&&|\|\||;|\||\n", text):
        try:
            words = shlex.split(part)
        except ValueError:
            continue
        if words:
            yield words


def block(reason):
    print(reason, file=sys.stderr)
    sys.exit(2)


def main():
    payload = json.load(sys.stdin)
    cwd = payload.get("cwd") or "."
    staged = None
    for words in commands(payload.get("tool_input", {}).get("command", "")):
        if words[0] == "cd" and len(words) > 1:
            cwd = words[1] if words[1].startswith("/") else f"{cwd}/{words[1]}"
            continue
        if words[0] != "git":
            continue
        here, args = cwd, words[1:]
        while args[:1] == ["-C"] and len(args) > 1:
            here, args = args[1] if args[1].startswith("/") else f"{here}/{args[1]}", args[2:]
        if not args:
            continue
        verb, rest = args[0], args[1:]
        branch = git(here, "branch", "--show-current")
        if verb == "push":
            targets = [arg for arg in rest if not arg.startswith("-")][1:]
            if any(t in ("main", "HEAD:main") or t.endswith(":main") or t.endswith(":refs/heads/main") for t in targets) \
                    or (not targets and branch == "main"):
                block("Blocked: never push to main (CLAUDE.md). Push a branch and open a pull request.")
        elif verb == "commit" and branch == "main":
            block("Blocked: never commit on main (CLAUDE.md). Create a branch first.")
        elif verb == "commit":
            staged = git(here, "diff", "--cached", "--name-status")
        new = None
        if verb in ("switch", "checkout"):
            for flag in ("-c", "-C", "-b", "-B", "--create"):
                if flag in rest and rest.index(flag) + 1 < len(rest):
                    new = rest[rest.index(flag) + 1]
        elif verb == "worktree" and rest[:1] == ["add"] and "-b" in rest and rest.index("-b") + 1 < len(rest):
            new = rest[rest.index("-b") + 1]
        if new and not new.startswith(PREFIXES):
            block(f"Blocked: branch '{new}' needs one of the prefixes {', '.join(PREFIXES)} (CLAUDE.md).")
    if staged is not None:
        print(json.dumps({"hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "additionalContext": "Files staged for this commit; check each one was changed on purpose:\n" + (staged or "(none)"),
        }}))


main()
