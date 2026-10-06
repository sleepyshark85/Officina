#!/usr/bin/env python3
"""Before a Bash command: blocks pushing to main, committing on main and branches without an allowed prefix, as
CLAUDE.md asks; before a commit, shows the staged files so an unexpected one is caught."""
import json
import os
import re
import shlex
import subprocess
import sys

PREFIXES = ("slice/", "docs/", "fix/", "refactor/", "chore/", "test/", "feature/")
OPERATORS = set(";&|\n")

# A heredoc's body is input to a command, never a command: from "<<WORD" to the line holding only WORD.
HEREDOC = re.compile(r"<<-?[ \t]*(['\"]?)(\w+)\1[^\n]*\n.*?\n[ \t]*\2[ \t]*(?=\n|$)", re.S)


def git(cwd, *args):
    run = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
    return run.stdout.strip() if run.returncode == 0 else ""


def commands(text):
    """Each simple command in the text, as words; quoted text, newlines included, stays one word."""
    lexer = shlex.shlex(HEREDOC.sub("", text), posix=True, punctuation_chars=";&|\n")
    lexer.whitespace = " \t\r"
    lexer.whitespace_split = True
    words = []
    for token in lexer:
        if token and set(token) <= OPERATORS:
            if words:
                yield words
            words = []
        else:
            words.append(token)
    if words:
        yield words


def block(reason):
    print(reason, file=sys.stderr)
    sys.exit(2)


def resolve(cwd, path):
    return os.path.normpath(path if os.path.isabs(path) else os.path.join(cwd, path))


def main():
    payload = json.load(sys.stdin)
    text = payload.get("tool_input", {}).get("command", "")
    cwd = payload.get("cwd") or "."
    branches = {}
    staged = None
    try:
        parsed = list(commands(text))
    except ValueError:
        # Unbalanced quotes: the shell will reject it too, but never let a commit on main through unread.
        if git(cwd, "branch", "--show-current") == "main" and re.search(r"\bgit\b[^\n]*\bcommit\b", text):
            block("Blocked: never commit on main (CLAUDE.md). Create a branch first.")
        return

    for words in parsed:
        if words[0] == "cd" and len(words) > 1:
            cwd = resolve(cwd, words[1])
            continue
        if words[0] != "git":
            continue
        here, args = cwd, words[1:]
        while args and args[0].startswith("-"):
            if args[0] in ("-C", "-c") and len(args) > 1:
                here = resolve(here, args[1]) if args[0] == "-C" else here
                args = args[2:]
            else:
                args = args[1:]
        if not args:
            continue
        verb, rest = args[0], args[1:]
        branch = branches.get(here) or git(here, "branch", "--show-current")
        positional = [arg for arg in rest if not arg.startswith("-")]

        if verb == "push":
            if "--all" in rest or "--mirror" in rest:
                block("Blocked: pushing every branch pushes main too (CLAUDE.md). Push the branch itself.")
            refspecs = positional[1:]
            targets = [spec.lstrip("+").split(":")[-1] for spec in refspecs] or [branch]
            targets = [branch if target in ("HEAD", "@") else target.removeprefix("refs/heads/") for target in targets]
            if "main" in targets:
                block("Blocked: never push to main (CLAUDE.md). Push a branch and open a pull request.")
        elif verb == "commit":
            if branch == "main":
                block("Blocked: never commit on main (CLAUDE.md). Create a branch first.")
            staged = git(here, "diff", "--cached", "--name-status")

        new = None
        if verb in ("switch", "checkout"):
            for flag in ("-c", "-C", "-b", "-B", "--create", "--force-create"):
                if flag in rest and rest.index(flag) + 1 < len(rest):
                    new = rest[rest.index(flag) + 1]
            if new is None and verb == "switch" and positional:
                branches[here] = positional[0]
        elif verb == "worktree" and rest[:1] == ["add"] and "-b" in rest and rest.index("-b") + 1 < len(rest):
            new = rest[rest.index("-b") + 1]
        elif verb == "branch" and len(positional) in (1, 2) and not any(arg.startswith("-") for arg in rest):
            new = positional[0]
        if new:
            # Resetting the local main to the remote's is normal; any other new branch needs a prefix.
            if new != "main" and not new.startswith(PREFIXES):
                block(f"Blocked: branch '{new}' needs one of the prefixes {', '.join(PREFIXES)} (CLAUDE.md).")
            if verb in ("switch", "checkout"):
                branches[here] = new

    if staged is not None:
        print(json.dumps({"hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "additionalContext": "Files staged for this commit; check each one was changed on purpose:\n" + (staged or "(none)"),
        }}))


main()
