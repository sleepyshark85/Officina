# Merge check

For a PR whose base moved: it is behind its base, conflicts with it, or was retargeted. Agent: `fixer`, with
`isolation: "worktree"`; a conflict that needs a design choice goes to `developer`.

```text
Bring PR #{{n}} (branch `{{branch}}`) up to date with `origin/{{base}}`{{, now that #N it was stacked on has merged, or nothing}}.
Merge `origin/{{base}}` into the branch; never rebase or force-push it. Resolve each conflict so both sides' changes
survive; where a side's intent is unclear, stop and report the conflict instead. Change nothing else. Run the tests
both sides' changes touch, then push.
Scratch prefix: `{{prefix}}-`. No live calls.

Report whether it merged cleanly, each conflict and how you resolved it, and the new head, which needs a new verdict.
Follow .claude/skills/dispatch/agent-rules.md.
```
