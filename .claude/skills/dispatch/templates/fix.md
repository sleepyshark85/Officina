# Fix brief

After a review asked for changes. Agent: `fixer` when every finding is small and its fix obvious, else `developer`;
either with `isolation: "worktree"`.

```text
Fix the review findings on PR #{{n}} (branch `{{branch}}`) from the verdict at {{sha}}: {{link to the verdict comment}}.
Fix {{the must-fix items by number, or "every must-fix"}}{{, and these nits, or nothing}}; change nothing else.
Read first: {{named sections the findings touch, or "the verdict and the files it names"}}.
Your files: {{paths}}. Other agents own {{paths and who, or "nothing: no one else is working"}}.
Scratch prefix: `{{prefix}}-`. Live API cap: {{"$X" or "no live calls"}}.

Each fix meets the code quality bar in docs/conventions.md and keeps or adds the test that shows it. Push to
`{{branch}}`, and answer each finding's review thread, where it has one, with the commit that fixes it. Follow
.claude/skills/dispatch/agent-rules.md.

Then, once CI is green, send the next round to PR #{{n}}'s reviewer, agent `{{reviewer id}}` (the same one for all its
rounds), with `SendMessage`, not through the lead: "Round {{N}} of 3 for PR #{{n}} at <full head sha>, replying to your
verdict at <sha>: <one line per fix>. Check CI, post a verdict naming the head, and reply in the short format." Wait
for the reply before you end, and finish with one line: the verdict and the head sha. Never set auto-merge.
```

The lead still dispatches every new reviewer, takes round 3's outcome to the owner, and alone sets auto-merge.
