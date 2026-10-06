#!/usr/bin/env python3
"""Before a Bash command: blocks pushing to main, committing on main and branches without an allowed prefix, as
CLAUDE.md asks. Before a commit that stages code it runs the format check and the build, and shows the staged files so
an unexpected one is caught; before a push of more than docs it runs the tests. CI runs the same checks; these find a
failure before the commit or push instead of after it."""
import json
import os
import re
import shlex
import subprocess
import sys

PREFIXES = ("slice/", "docs/", "fix/", "refactor/", "chore/", "test/", "feature/")
OPERATORS = set(";&|\n")
CODE = re.compile(r"\.(cs|csproj|props|targets|slnx|editorconfig|json)$")
DOCS = re.compile(r"\.(md|html|svg|png)$")

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


def check(root, command, failure):
    """Runs a check in the repository's root; a failure blocks the command with the check's last lines."""
    run = subprocess.run(command, cwd=root, capture_output=True, text=True)
    if run.returncode != 0:
        tail = "\n".join((run.stdout + run.stderr).strip().splitlines()[-30:])
        block(f"{failure}\n{tail}")


def before_commit(here):
    """The format check and the build, when the commit stages code; both read the working tree, so every staged code
    file must have no unstaged changes."""
    staged = git(here, "diff", "--cached", "--name-only").splitlines()
    code = [path for path in staged if CODE.search(path)]
    if not code:
        return
    partly = set(code) & set(git(here, "diff", "--name-only").splitlines())
    if partly:
        block("Blocked: these staged files also have unstaged changes; stage or stash them first:\n" + "\n".join(sorted(partly)))
    root = git(here, "rev-parse", "--show-toplevel")
    check(root, ["dotnet", "format", "--verify-no-changes"], "Blocked: the format check fails; run 'dotnet format', then commit again.")
    check(root, ["dotnet", "build", "--configuration", "Release", "--nologo", "--verbosity", "quiet"],
          "Blocked: the build fails (warnings are errors); fix it, then commit again.")


def before_push(here, branch):
    """The tests, when the commits the push would send change more than docs."""
    base = git(here, "merge-base", "origin/main", branch or "HEAD")
    changed = git(here, "diff", "--no-renames", "--name-only", base, branch or "HEAD").splitlines() if base else ["?"]
    if any(not DOCS.search(path) for path in changed):
        check(git(here, "rev-parse", "--show-toplevel"), ["dotnet", "test", "--configuration", "Release", "--nologo",
              "--verbosity", "quiet", "--blame-hang-timeout", "2m"], "Blocked: the tests fail; fix them, then push again.")


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
    checks = []
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
            checks.append(lambda here=here, branch=branch: before_push(here, branch))
        elif verb == "commit":
            if branch == "main":
                block("Blocked: never commit on main (CLAUDE.md). Create a branch first.")
            staged = git(here, "diff", "--cached", "--name-status")
            checks.append(lambda here=here: before_commit(here))

        new = None
        if verb in ("switch", "checkout"):
            for flag in ("-c", "-C", "-b", "-B", "--create", "--force-create"):
                if flag in rest and rest.index(flag) + 1 < len(rest):
                    new = rest[rest.index(flag) + 1]
            # Moving to an existing branch; checkout also takes paths, so only a name that is a branch counts.
            target = positional[0] if positional else None
            if new is None and target and "--" not in rest and (verb == "switch" or len(positional) == 1) \
                    and any(git(here, "rev-parse", "--verify", "--quiet", ref) for ref in (f"refs/heads/{target}", f"refs/remotes/origin/{target}")):
                branches[here] = target
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

    # Run only once every command in the line has passed the rules above.
    for run in checks:
        run()
    if staged is not None:
        print(json.dumps({"hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "additionalContext": "Files staged for this commit; check each one was changed on purpose:\n" + (staged or "(none)"),
        }}))


main()
