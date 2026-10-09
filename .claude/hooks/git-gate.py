#!/usr/bin/env python3
"""Before a Bash command: blocks pushing to main, committing on main and branches without an allowed prefix, as
docs/conventions.md asks. Before a commit that stages code it runs that implementation's format check and build (.NET
at the root, Go under go/), and shows the staged files so an unexpected one is caught; before a push of more than docs
it runs the tests of each implementation it changes. CI runs the same checks; these find a failure before the commit
or push instead of after it."""
import json
import os
import re
import shlex
import shutil
import subprocess
import sys

PREFIXES = ("slice/", "docs/", "fix/", "refactor/", "chore/", "test/", "feature/")
OPERATORS = set(";&|\n")
CODE = re.compile(r"\.(cs|csproj|props|targets|slnx|editorconfig|json)$")
GO_CODE = re.compile(r"(\.go|(^|/)go\.(mod|sum)|\.golangci\.yml)$")
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


def check(cwd, command, failure, env=None):
    """Runs a check; a failure blocks the command with the check's last lines. Returns what the check printed."""
    run = subprocess.run(command, cwd=cwd, capture_output=True, text=True, env=env)
    if run.returncode != 0:
        tail = "\n".join((run.stdout + run.stderr).strip().splitlines()[-30:])
        block(f"{failure}\n{tail}")
    return run.stdout


def is_go(path):
    return path.startswith("go/")


def go_env():
    """The environment for the Go checks: the go command on PATH, and the tools it installed (golangci-lint) found in
    its GOPATH's bin."""
    go = shutil.which("go")
    if not go:
        block("Blocked: the Go checks need the go command on PATH (go/README.md).")
    gopath = subprocess.run([go, "env", "GOPATH"], capture_output=True, text=True).stdout.strip()
    env = dict(os.environ)
    env["PATH"] = os.pathsep.join([env.get("PATH", ""), *(os.path.join(p, "bin") for p in gopath.split(os.pathsep) if p)])
    if not shutil.which("golangci-lint", path=env["PATH"]):
        block("Blocked: the Go checks need golangci-lint on PATH or in GOPATH's bin (go/README.md).")
    return env


def before_commit(here):
    """Each implementation's format check and build, when the commit stages its code: .NET's outside go/, Go's under
    it. They read the working tree, so every staged code file must have no unstaged changes."""
    staged = git(here, "diff", "--cached", "--name-only").splitlines()
    dotnet = [path for path in staged if CODE.search(path) and not is_go(path)]
    go = [path for path in staged if GO_CODE.search(path) and is_go(path)]
    if not dotnet and not go:
        return
    partly = set(dotnet + go) & set(git(here, "diff", "--name-only").splitlines())
    if partly:
        block("Blocked: these staged files also have unstaged changes; stage or stash them first:\n" + "\n".join(sorted(partly)))
    root = git(here, "rev-parse", "--show-toplevel")
    if dotnet:
        check(root, ["dotnet", "format", "--verify-no-changes"], "Blocked: the format check fails; run 'dotnet format', then commit again.")
        check(root, ["dotnet", "build", "--configuration", "Release", "--nologo", "--verbosity", "quiet"],
              "Blocked: the build fails (warnings are errors); fix it, then commit again.")
    if go:
        env, module = go_env(), os.path.join(root, "go")
        unformatted = check(module, ["gofmt", "-l", "."], "Blocked: gofmt fails.", env).strip()
        if unformatted:
            block("Blocked: these files are not formatted; run 'gofmt -w .' in go/, then commit again:\n" + unformatted)
        check(module, ["go", "vet", "./..."], "Blocked: go vet fails; fix it, then commit again.", env)
        check(module, ["golangci-lint", "run", "./..."],
              "Blocked: golangci-lint fails (go/.golangci.yml); fix it, then commit again.", env)
        check(module, ["go", "build", "./..."], "Blocked: the Go build fails; fix it, then commit again.", env)


def before_push(here, branch):
    """The tests of each implementation the push's commits change beyond docs: .NET's outside go/, Go's under it."""
    base = git(here, "merge-base", "origin/main", branch or "HEAD")
    changed = git(here, "diff", "--no-renames", "--name-only", base, branch or "HEAD").splitlines() if base else ["?", "go/?"]
    code = [path for path in changed if not DOCS.search(path)]
    root = git(here, "rev-parse", "--show-toplevel")
    if any(not is_go(path) for path in code):
        check(root, ["dotnet", "test", "--configuration", "Release", "--nologo",
              "--verbosity", "quiet", "--blame-hang-timeout", "2m"], "Blocked: the tests fail; fix them, then push again.")
    if any(is_go(path) for path in code):
        check(os.path.join(root, "go"), ["go", "test", "-race", "-shuffle=on", "./..."],
              "Blocked: the Go tests fail; fix them, then push again.", go_env())


def stages_all(rest):
    """Whether commit options stage every tracked change themselves: -a, --all, or -a in a cluster such as -am."""
    for arg in rest:
        if arg in ("-a", "--all"):
            return True
        if re.fullmatch(r"-[A-Za-z]+", arg):
            for flag in arg[1:]:
                if flag == "a":
                    return True
                if flag in "mFCct":
                    break
    return False


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
    staging = False
    try:
        parsed = list(commands(text))
    except ValueError:
        # Unbalanced quotes: the shell will reject it too, but never let a commit on main through unread.
        if git(cwd, "branch", "--show-current") == "main" and re.search(r"\bgit\b[^\n]*\bcommit\b", text):
            block("Blocked: never commit on main (docs/conventions.md). Create a branch first.")
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
                block("Blocked: pushing every branch pushes main too (docs/conventions.md). Push the branch itself.")
            refspecs = positional[1:]
            targets = [spec.lstrip("+").split(":")[-1] for spec in refspecs] or [branch]
            targets = [branch if target in ("HEAD", "@") else target.removeprefix("refs/heads/") for target in targets]
            if "main" in targets:
                block("Blocked: never push to main (docs/conventions.md). Push a branch and open a pull request.")
            sources = [spec.lstrip("+").split(":")[0] for spec in refspecs]
            pushed = branch if not sources or sources[0] in ("", "HEAD", "@") else sources[0]
            checks.append(lambda here=here, pushed=pushed: before_push(here, pushed))
        elif verb in ("add", "rm", "mv"):
            staging = True
        elif verb == "commit":
            if branch == "main":
                block("Blocked: never commit on main (docs/conventions.md). Create a branch first.")
            # This hook runs before the whole line, so it sees the index only as it was before the line ran.
            if staging or stages_all(rest):
                block("Blocked: stage files in their own command before committing (no add/rm/mv in the same line, "
                      "no commit -a), so the checks see what the commit holds.")
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
                block(f"Blocked: branch '{new}' needs one of the prefixes {', '.join(PREFIXES)} (docs/conventions.md).")
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
