---
name: dispatch
description: Writes the brief for an Officina agent from a template, so every dispatch carries the same fixed requirements. Use whenever the lead sends a developer, fixer or reviewer agent to build part of a slice, fix review findings, review a pull request, or bring a pull request up to date with its base.
argument-hint: "[developer|fix|review|merge-check]"
---

# Dispatch an agent

Kind asked for: `$0` (`developer`, `fix`, `review` or `merge-check`).

1. Open the kind's template: [developer](templates/developer.md), [fix](templates/fix.md),
   [review](templates/review.md), [merge-check](templates/merge-check.md). It names the agent to use and holds the
   brief.
2. Fill every `{{…}}` placeholder. Don't copy rules into the brief: it points to
   [docs/conventions.md](../../../docs/conventions.md), the language rules and [agent-rules.md](agent-rules.md).
3. Check the brief before sending it:
   - **Sections:** named ones (`ARCHITECTURE §5.1–§5.3`, `docs/implementations/ruby.md R9`), never a whole document.
   - **Branch and base:** the base is the branch the PR sits on, and a stacked one names that PR.
   - **Files:** with agents working in parallel, each brief lists its own files, and no two briefs share one.
   - **Scratch prefix:** unique among the agents running now (the slice id, plus the part or round).
   - **Live cost cap:** a dollar figure, or "no live calls".
4. Send it with the Agent tool, as the template says. When the agent reports, merging is the lead's step, under
   [How we work](../../../docs/conventions.md#how-we-work).
