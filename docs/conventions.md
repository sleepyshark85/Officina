# Officina conventions

How we work on Officina in every implementation (.NET at the repository root, Go in [`go/`](../go/), Ruby in
[`ruby/`](../ruby/)). Each implementation adds its own language rules: [`CLAUDE.md`](../CLAUDE.md) for .NET,
[`go/CLAUDE.md`](../go/CLAUDE.md) for Go, [`ruby/CLAUDE.md`](../ruby/CLAUDE.md) for Ruby. Its platform decisions are
in [`docs/implementations/`](implementations/). Where the two disagree, the language's own rules win for how code is
written, and this page wins for what the code must do.

## How we work

- **Every change goes through a branch and a pull request** to `main`, docs included. Never push to `main`. Merge
  only when the Opus reviewer has approved and the required checks pass (the implementation names them), the branch
  is up to date and the review threads are resolved; the owner has delegated that go-ahead.
  Branches: `slice/<id>-<slug>`, `docs/<topic>`, or `fix/`, `refactor/`, `chore/`, `test/` or `feature/` and a slug.
  Retarget a stacked PR before deleting the branch it is based on (deleting a base closes the PR).
- **Each implementation's CI runs only when the pull request changes it**: `.github/changes.py` says what belongs to
  which (Go's and Ruby's folders, spikes and workflows; the shared `testdata/` to all; the rest to .NET). The other
  implementations' jobs are skipped, which their required checks count as passing; never add a path filter, as a
  required check that never reports blocks the merge. A job is skipped only when the script says false, so a failed
  detector runs everything rather than passing it untested.
- **Claude Code hooks enforce the mechanical rules** (`.claude/settings.json`, scripts in `.claude/hooks/`): no push
  to `main`, no commit on `main`, branch prefixes, and each implementation's format, build and test checks. Stage
  files in their own command before `git commit` (no `add` in the same line, no `commit -a`): the hook runs before the
  line, so it sees only what is already staged. A blocked action says why; fix the cause, never work around the hook.
- **The simplest thing that works.** Build only what the slice's acceptance criteria need: no abstraction without a
  current user, no setting without a known case (until then, a constant), no optimization without a measured target.
  Prefer a platform or standard-library feature over custom code. Simplicity never at the cost of separation of
  concerns or clear design.
- **Short design docs:** tables and diagrams over prose; cite requirement IDs instead of restating them.
  `ARCHITECTURE.md` and `REQUIREMENTS.md` name no language, type or API: they are shared by every implementation.
- **Short code comments:** say what the code cannot, briefly; no requirement IDs or doc section numbers. Test names
  carry the IDs, and each implementation's traceability page maps them.
- **Who codes and reviews:** the agents in `.claude/agents/`: `developer` (Opus) for big or risky slices, `fixer`
  (Sonnet) only for small, well-bounded fixes, and `reviewer` (Opus), which approves each PR with a verdict comment the
  review gate reads, checking these conventions, the implementation's language rules, the design rules below and
  over-complication; at most 3 review rounds, then stop and summarize for the owner.
- Review comments may arrive as a pending review: read them with GraphQL
  `pullRequest(number: N) { reviewThreads { … } }`, as the REST endpoints don't return them.

## Tests

- **Tests replace only system boundaries:** the model provider (scripted model), network and MCP servers, the clock,
  environment and secrets, the human (scripted approver), storage back ends where a real one is impractical. Everything
  inside Officina is tested with real objects. Prefer a real database in Docker over a faked one for the application.
- **Tests are the agent's check on its own work.** Example tests say what should happen; property tests say what
  must never happen. Tests are fast and deterministic: a flaky test, or one slow enough to notice (over a second
  offline), is a bug to fix, never something to retry.

## Code quality bar

**The bar is good code, not working code.** The owner set it on 2026-10-10: low or even mediocre code never passes
review, however correct it is and whatever the checks say. It holds in every implementation, on top of the language
rules ([`CLAUDE.md`](../CLAUDE.md), [`go/CLAUDE.md`](../go/CLAUDE.md), [`ruby/CLAUDE.md`](../ruby/CLAUDE.md)). The
reviewer applies it to every changed line as its next maintainer would read it, and anything below is a must-fix.

| Area | Must-fix |
|---|---|
| Names and comments | A name that doesn't say what a thing is or does. A comment that restates the code, or code so unclear it needs a comment to say what it does; a comment saying *why* is welcome |
| Structure | Duplication; a method doing more than one thing; deep nesting; a long parameter list; a table padded with filler entries; a lint or analyzer rule disabled or suppressed (a RuboCop cop, a `nolint`, a warning pragma) to let any of these through |
| Errors | Rescuing or catching too broadly; swallowing an error; losing its cause; turning a bug into a silent default; treating a permanent failure as transient |
| Concurrency and resources | A thread, task or process without a clear owner, guard or join; a resource (stream, connection, file, child process) not released on every path, `break` and exceptions included |
| Idiom and size | Code that fights the language's idiom; a workaround larger or wider than the problem it works around; an abstraction, setting or option without a current user |
| Tests | A test that passes for another reason than its name, or that would still pass if the behaviour it names broke; an error test that checks only the class, not the message; a test with logic (loops, branches) instead of focused cases; packing several values into one assertion to dodge an assertion limit |
| Mutation | A surviving mutant in the code a PR changes. Kill it, delete the code that makes it equivalent, or show in the PR description why it is equivalent (or exclude it in the mutation tool's config with that reason), and the reviewer judges each. CI's mutation thresholds are a floor, not the bar |

How it is applied:

- **"It works" or "a reviewer accepted it before" is never a reason to approve** code the reviewer would not write. A
  later round may raise a must-fix an earlier one missed, once, within the same three rounds.
- **A rule turned off needs a reason the reviewer agrees with**, on the line that turns it off. "To make the check
  pass" is not one.
- **Claims are reproduced, not trusted.** A constraint reported by an agent (a tool that "cannot" do something, a
  survivor that is "equivalent") becomes a rule or an exclusion only once it is shown.
- **Nits are true trivia** (wording, a redundant character); they never block. Everything in the table above is a
  must-fix, never a nit.
- Code merged before the bar was set is audited against it and brought up to it in its own PRs.

## Design rules that code must keep

- **One architecture, every language.** The layers, the data flow and the runtime model (streaming, event order,
  concurrency, cancellation, persistence points) are defined once, in ARCHITECTURE.md §3, §5 and §12. Each
  implementation realizes them in its own idiom, and its design notes map every row of §5.3 to its mechanism
  ([`docs/design/`](design/README.md) for .NET, [`go/docs/design.md`](../go/docs/design.md),
  [`ruby/docs/design.md`](../ruby/docs/design.md)). A change to a shared rule changes ARCHITECTURE.md first.
- **One primitive:** a run of one agent. Everything else composes runs.
- **Structured signals decide:** stop reasons, tool calls, validated output. Never parse free text to decide.
- **Append-only conversation:** the core never edits, reorders or drops a message. Compaction and clearing old tool
  results run on the provider's side only.
- **Stable prefix:** tools (deterministic order), instructions (frozen) and model settings are fixed per conversation,
  enforced by a fingerprint. Per-run context is appended after the prefix as an operator message, never put into the
  instructions. Memory is a tool, never part of the instructions.
- **Every run ends in a result:** completed, stopped (with a reason) or failed. Tool errors go back to the model.
- **A write tool never runs unaudited** when the agent has an audit sink: its attempt is recorded before it runs, and
  it does not run if that fails. Without a sink there is no trail at all (GEN-02).
- **Purpose-neutral core:** no domain concepts, UI or transport in the core, and no storage it picks itself: its
  built-in stores (file and in-memory memory, JSON-lines audit) run only when the host chooses them. Everything except
  model and instructions is optional.
- **Dependencies:** no agent framework or general AI abstraction library. The provider's SDK is used only in the
  Claude package. The core depends on nothing beyond what the dependency rule allows (ARCHITECTURE §3, D15); a
  dependency check test enforces both.
- **Each package wires its own services**, and the application composes them in one place; tests compose the same way
  and replace only the boundaries. How is the implementation's choice (D15).

## Claude API notes

Current models and features move fast; check the `claude-api` skill before writing provider code. Key points for
Opus 5.5 (the default model): thinking can't be disabled (set effort explicitly, its default is `medium`); forced
`tool_choice` is rejected; reasoning blocks are bound to the exact prefix that produced them, so history must stay
append-only; use the beta message types only (agentic-core spike S00b). A provider feature is proven per SDK: one
that works in one language's SDK may not be exposed yet in another's.
