# Developer brief

For a slice or part of one. Agent: `developer`, with `isolation: "worktree"`. A small fix with an obvious approach
goes to [fix](fix.md) instead.

```text
Build {{implementation}} {{slice}}{{ part, e.g. " part A: the schema DSL", or nothing}}.
Acceptance criteria: docs/plan/phase-1.md, the {{slice}} row of Slices and its {{implementation}} column of Per
implementation{{; what this part leaves to the other, or nothing}}.

Branch `{{branch}}` from `origin/{{base}}`; open the PR against `{{base}}`{{, stacked on #N, or nothing}}.
Read first: {{named sections, e.g. "REQUIREMENTS TOOL-01…06; ARCHITECTURE §4.2, §5.2, §5.3; docs/implementations/ruby.md R9, R19"}}.
Your files: {{paths}}. Other agents own {{paths and who, or "nothing: no one else is working"}}.
Scratch prefix: `{{prefix}}-`. Live API cap: {{"$X for the whole task" or "no live calls"}}.

The review applies every row of the code quality bar in docs/conventions.md. Follow
.claude/skills/dispatch/agent-rules.md.
{{"Stop once the PR is open: the lead dispatches its reviewer." or "Then ask a new `reviewer` agent to review it, and fix what it finds."}}
```
