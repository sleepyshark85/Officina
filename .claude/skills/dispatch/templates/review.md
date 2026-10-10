# Reviewer brief

Agent: `reviewer`, new or continued as [Who codes and reviews](../../../../docs/conventions.md#how-we-work) says.

A new reviewer (a first round, or a strict re-review) gets:

```text
Review PR #{{n}}, round {{1, or the next round for a strict re-review}} of 3: {{implementation}} {{slice and part, or what the PR changes}}. Head {{full sha}}, base `{{base}}`{{, stacked on #N, or nothing}}.
Acceptance criteria: {{"docs/plan/phase-1.md, the S0x row and its column", or the task the PR answers}}.
Read: {{named sections, e.g. "ARCHITECTURE §5.1–§5.3, §12.1; ruby/CLAUDE.md; docs/implementations/ruby.md R10"}}.
{{What needs a close look, e.g. "the cancellation path" or "its claim that two survivors are equivalent", or nothing}}
Scratch prefix: `{{prefix}}-`. Live API cap: {{"$X" or "no live calls"}}.

You review this PR only. Apply the code quality bar in docs/conventions.md strictly, to every changed line. Post the
verdict as your agent definition says. Follow .claude/skills/dispatch/agent-rules.md.
```

The same reviewer's later rounds, by `SendMessage` from the lead or from the agent that fixed the verdict (see
[fix](fix.md)):

```text
Round {{2 or 3}} of 3 for PR #{{n}} at {{full head sha}}, replying to your verdict at {{sha}}: {{one line per fix}}. Check CI, post a verdict naming the head, and reply in the short format.
```
