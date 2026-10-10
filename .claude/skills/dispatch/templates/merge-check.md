# Merge check

For a PR whose base moved: it is behind its base, conflicts with it, or was retargeted. The agent resolves any
conflicts, so the lead never reads a hunk. Agent: `fixer`, with `isolation: "worktree"`; a conflict that needs a
design choice goes to `developer`.

```text
Bring PR #{{n}} (branch `{{branch}}`) up to date with `origin/{{base}}`{{, now that #N it was stacked on has merged, or nothing}}.
Merge `origin/{{base}}` into the branch; never rebase or force-push it. Resolve each conflict so both sides' changes
survive; where a side's intent is unclear, stop and report the conflict instead. Change nothing else. Run the tests
both sides' changes touch, then push.
Scratch prefix: `{{prefix}}-`. No live calls.

Put each conflict and how you resolved it in a PR comment. Report in the short format of agent-rules.md, with
`Verdict:` the `review` status of the new head: the gate carries an approval over a clean merge, and anything else
needs a new verdict.
Follow .claude/skills/dispatch/agent-rules.md.
```

Where the PR's reviewer is not a new agent for the round, the merge-check follows the fix brief's pattern: the lead
names the reviewer's id, and the agent sends the round with `SendMessage` and ends with the verdict and head sha.
