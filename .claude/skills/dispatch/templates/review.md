# Reviewer brief

Agent: a new `reviewer` for each PR, kept for that PR's rounds only, as [Who codes and
reviews](../../../../docs/conventions.md#how-we-work) says.

First round, to the new agent:

```text
Review PR #{{n}}: {{implementation}} {{slice and part, or what the PR changes}}. Head {{full sha}}, base `{{base}}`{{, stacked on #N, or nothing}}.
Acceptance criteria: {{"docs/plan/phase-1.md, the S0x row and its column", or the task the PR answers}}.
Read: {{named sections, e.g. "ARCHITECTURE §5.1–§5.3, §12.1; ruby/CLAUDE.md; docs/implementations/ruby.md R10"}}.
{{What needs a close look, e.g. "the cancellation path" or "its claim that two survivors are equivalent", or nothing}}
Scratch prefix: `{{prefix}}-`. Live API cap: {{"$X" or "no live calls"}}.

You review this PR only. Apply the code quality bar in docs/conventions.md strictly, to every changed line. Post the
verdict as your agent definition says. Follow .claude/skills/dispatch/agent-rules.md.
```

Later rounds of the same PR, by `SendMessage` to that agent:

```text
Round {{2 or 3}} of 3 for PR #{{n}}: the head is now {{full sha}}, which answers your verdict at {{sha}}. Review what
changed since then and anything it could break, and post a verdict naming the new head.
```
