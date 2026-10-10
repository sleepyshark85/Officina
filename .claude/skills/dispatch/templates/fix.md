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

Then, once CI is green, send reviewer `{{reviewer id}}` (PR #{{n}}'s, the same for all its rounds) the later-round
text of review.md with `SendMessage`, one line per fix filled in. When the reply resumes you, finish with the verdict
and the head sha, plus the `Needs the lead` lines from your work and from the reply, copied as written.
```
