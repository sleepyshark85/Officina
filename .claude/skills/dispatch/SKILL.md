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
4. Send it with the Agent tool, as the template says. When the agent reports, the merge is the lead's step (auto-merge, below), under
   [How we work](../../../docs/conventions.md#how-we-work).

## Lead's routine

- Never read diffs, conflict hunks, whole files or full logs yourself: use `head` or `grep`, or dispatch a
  merge-check, a fixer or a reviewer, and act on its short report ([agent-rules.md](agent-rules.md), *Report*).
- Update your status note (the project memory's status file, such as `ruby-port-status`) only when the context hook
  warns at 150k, before `/compact`, and at the end of a session; the plan's Progress row goes in the next PR that
  touches the plan. At the end of a session also regenerate `docs/agent-usage.md`
  (`python3 -B scripts/agent-usage.py`), through a docs PR.
- Background watchers print only events (a PR merged, closed or behind), never progress.
- Set auto-merge (`gh pr merge N --auto --merge --delete-branch`) in the same step as reading an approving report
  with nothing in `Needs the lead`. The required `review` check means it merges only on an approving verdict on its
  head; to doubt an approval, ask for a strict re-review instead.
- Handle each report in one step: decide and send the next brief.
- Compact at the 150k warning, not earlier (the owner's choice): update the note and ask the owner to run `/compact`.
  Start a new session per batch of parallel slices.
- Review rounds run without the lead: the fixer sends each next round to the PR's reviewer ([fix](templates/fix.md)).
  Only the lead dispatches a new reviewer, takes round 3's outcome to the owner and sets auto-merge.
- Noted improvements, not adopted: [later.md](later.md).
