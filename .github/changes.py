#!/usr/bin/env python3
"""Prints `<implementation>=true` when a pull request changes that implementation (dotnet, go or ruby), else
`<implementation>=false`, for a workflow's `changes` job to put in $GITHUB_OUTPUT. Each workflow runs on every pull
request and skips its jobs on false, which a required check counts as passing. Outside a pull request it prints true.

A path belongs to Go or Ruby when it is under its folder, its spike or its workflow; the shared testdata/, the
Bookshop environment in bookshop/ (compose file, schema and seed, export server) and this script belong to every
implementation; anything else belongs to .NET, at the repository root."""
import os
import subprocess
import sys

OWN = {
    "go": ("go/", "spikes/go-", ".github/workflows/go.yml"),
    "ruby": ("ruby/", "spikes/ruby-", ".github/workflows/ruby.yml"),
}
SHARED = ("testdata/", "bookshop/", ".github/changes.py")


def owners(path):
    if path.startswith(SHARED):
        return {"dotnet", *OWN}
    return {name for name, prefixes in OWN.items() if path.startswith(prefixes)} or {"dotnet"}


def main():
    implementation = sys.argv[1]
    if os.environ.get("GITHUB_EVENT_NAME") != "pull_request":
        changed = True
    else:
        base = f"origin/{os.environ['GITHUB_BASE_REF']}"
        # Without --no-renames a file moved between implementations lists only its new path, hiding the side it left.
        paths = subprocess.run(["git", "diff", "--no-renames", "--name-only", f"{base}...HEAD"],
                               capture_output=True, text=True, check=True).stdout.splitlines()
        changed = any(implementation in owners(path) for path in paths)
    if not changed:
        print(f"Nothing of {implementation} changed; its jobs are skipped.", file=sys.stderr)
    print(f"{implementation}={'true' if changed else 'false'}")


main()
