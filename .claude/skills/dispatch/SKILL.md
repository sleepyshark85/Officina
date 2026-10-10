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

## Lead's routine

- Never read diffs, conflict hunks or full logs yourself: dispatch a merge-check, a fixer or a reviewer, and act on
  its short report ([agent-rules.md](agent-rules.md), *Report*).
- Update your status note (the project memory's status file, such as `ruby-port-status`) only when the context hook
  warns at 150k, before `/compact`, and at the end of a session; the plan's Progress row goes in the next PR that
  touches the plan. At the end of a session also regenerate `docs/agent-usage.md`
  (`python3 -B scripts/agent-usage.py`), through a docs PR.
- Never read whole files or logs in the lead: use `head` or `grep`, or ask an agent.
- Background watchers print only events (a PR merged, closed or behind), never progress.
- Set auto-merge (`gh pr merge N --auto --merge --delete-branch`) as soon as a PR opens. The required `review` check
  means it merges only on an approving verdict on its head.
- Handle each report in one step: decide and send the next brief.
- Compact at the 150k warning, not earlier (the owner's choice): update the note and ask the owner to run `/compact`.
  Start a new session per wave.
- Review rounds run without the lead: the fixer sends each next round to the PR's reviewer (see [fix](templates/fix.md)).
  The lead still dispatches every new reviewer, takes round 3's outcome to the owner, and alone sets auto-merge.

## Later

Improvements noted, not adopted yet.

- **Code orchestrator, model for judgement:** a script or state machine runs the PR pipeline (setting auto-merge,
  updating branches, watching merges, relaying rounds, starting a slice once the PR it depends on merges). It wakes a
  model only for events that need judgement (must-fixes, an agent's "needs the lead", round 3, a failing check), each
  with a fresh, small context holding only that PR's state; possibly built on the Claude Agent SDK. Planned after Ruby,
  before the .NET/Go audit or phase 2.
- **Update branches without the lead:** a GitHub workflow on push to main updates BEHIND auto-merge PRs, replacing the
  lead's 4-hour background keeper. Or GitHub's merge queue, once the review gate supports `merge_group`.
- **Fewer conflicting merges:** plan parallel slices so that no two edit the same shared files at once, or stack them.
  #117 needed three merges of main and two merge checks.
- **Environment notes for agents:** one file with this machine's facts that live briefs point to (the compose database
  on 5433, another project's Postgres on 5432). Two live runs were wasted on it.
- **Mechanical mutation in the app:** a mutant config for ruby/apps/bookshop and the test kit, so reviewers stop
  mutating app lines by hand.
- **Cheaper agents where the work allows:** the fixer (Sonnet) for any well-bounded follow-up, the developer only for
  slices.
- **A session-end reminder hook** that prompts the agent-usage regeneration.
