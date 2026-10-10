#!/usr/bin/env python3
"""Decides a pull request's `review` status from its verdict comments and prints `<state>\t<description>`.

Usage: review_gate.py <head sha> <base branch> < comments.json

comments.json is the pull request's issue comments as the API returns them, oldest first. It runs in a clone of the
repository holding the head commit and `refs/remotes/origin/<base branch>`, and only reads them with git plumbing: it
never checks out or runs the pull request's code.

A verdict is a comment starting "**Verdict:" by the owner, a member or a collaborator; it names every commit whose full
sha it contains. The newest verdict naming the head decides, and passes only as "**Verdict: APPROVE** at <head>".
Without one, an approval of an earlier commit A of the pull request carries over to the head when it is the newest
verdict naming any of the pull request's commits, every commit after A is a merge of the base branch, and the head is
exactly git's clean merge of A's change onto the base, with the same diff as A. Anything else waits for a verdict
naming the head."""
import json
import re
import subprocess
import sys

ALLOWED_AUTHORS = {"OWNER", "MEMBER", "COLLABORATOR"}
APPROVAL = re.compile(r"\*\*Verdict: APPROVE\*\* at ([0-9a-f]{40})")


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout


def decide(comments, head, base):
    """The status for the head: a (state, description) pair."""
    verdicts = [comment["body"] for comment in comments
                if comment["author_association"] in ALLOWED_AUTHORS and comment["body"].startswith("**Verdict:")]
    at_head = [verdict for verdict in verdicts if head in verdict]
    if at_head:
        if approved_commit(at_head[-1]) == head:
            return "success", f"Approved at {head[:7]}"
        return "failure", f"Changes requested at {head[:7]}"

    waiting = f"Waiting for a verdict naming {head[:7]} in full"
    base_ref = f"refs/remotes/origin/{base}"
    own_commits = git("rev-list", head, "--not", base_ref).split()
    on_own_commits = [verdict for verdict in verdicts if any(commit in verdict for commit in own_commits)]
    if not on_own_commits:
        return "pending", waiting
    approved = approved_commit(on_own_commits[-1])
    if approved not in own_commits:
        return "pending", f"{waiting}; the newest verdict approves none of its commits"
    reason = why_not_carried(approved, head, base_ref)
    if reason:
        return "pending", f"{waiting}; {reason}"
    return "success", f"Approval at {approved[:7]} carried over clean merges of {base}"


def approved_commit(verdict):
    match = APPROVAL.match(verdict)
    return match and match.group(1)


def why_not_carried(approved, head, base_ref):
    """Why the approval of an earlier commit does not hold for the head, or None when it does."""
    for line in git("rev-list", "--parents", head, "--not", approved, base_ref).splitlines():
        commit, *parents = line.split()
        # As the head reaches the approved commit, the first parents of these merges lead back to it, with nothing of
        # the author's in between.
        if len(parents) != 2 or not is_ancestor(parents[1], base_ref):
            return f"{commit[:7]} is not a merge of the base"
    head_base = git("merge-base", head, base_ref).strip()
    if replay(approved, onto=head_base) != git("rev-parse", f"{head}^{{tree}}").strip():
        return f"its content is not a clean merge of {approved[:7]}"
    # The content is the approved change; this also asks for a new verdict when the base changed the lines around it
    # or already made part of it.
    if patch_id(approved, base_ref) != patch_id(head, base_ref):
        return f"its diff differs from {approved[:7]}"
    return None


def is_ancestor(commit, of):
    return subprocess.run(["git", "merge-base", "--is-ancestor", commit, of]).returncode == 0


def replay(commit, onto):
    """The tree of git's merge of a commit onto a commit of the base, or None when it conflicts."""
    merge = subprocess.run(["git", "merge-tree", "--write-tree", onto, commit], capture_output=True, text=True)
    if merge.returncode == 1:
        return None
    merge.check_returncode()
    return merge.stdout.split()[0]


def patch_id(commit, base_ref):
    """The patch id of the pull request's diff at a commit, whitespace included."""
    diff = subprocess.run(["git", "diff", git("merge-base", commit, base_ref).strip(), commit],
                          capture_output=True, check=True).stdout
    return subprocess.run(["git", "patch-id", "--verbatim"], input=diff, capture_output=True, check=True).stdout


def main():
    head, base = sys.argv[1:]
    state, description = decide(json.load(sys.stdin), head, base)
    print(f"{state}\t{description}")


main()
