# Officina — working notes for Claude sessions

Officina is a purpose-neutral .NET 10 library for building agentic applications. Packages are named under
`Sleepyshark.Officina`. Phase 1 is accepted against one reference application, **Bookshop Assistant**: an interactive
console chatbot over PostgreSQL in Docker.

It replaces `~/sources/agentic-core` (the first Officina, built top-down for a coding team, now archived). Its Claude
provider, MCP client, test kit and spike findings (`docs/spikes/` there) are reused: see ARCHITECTURE §13.

## Where things are

- `REQUIREMENTS.md`: what to build (IDs such as `TOOL-03`), phase 1 and north star, with decisions in §7.
- `ARCHITECTURE.md`: concepts, components, contracts and flows. **It holds no code, type names or API names;** keep it
  that way. Implementation detail goes in code and its comments.
- `docs/design/`: the type-level view (class, package and sequence diagrams, principles and trade-offs). Unlike
  ARCHITECTURE.md it names types, so update it when a change moves what it shows. Each diagram's `.html` is the source;
  the `.svg` beside it is exported from it.

## Start here (not done yet)

1. Done: `git init`, README, first commit of the docs; GitHub repository `sleepyshark85/officina` (public).
2. A phase 1 plan in `docs/plan/`: small vertical slices, each ending in something that runs, with offline tests.
   Suggested order: skeleton and dependency check → live check of the Claude features not yet proven by spike S00b
   (server-side compaction, tool-result clearing, the memory tool, thinking display `updates`, on the beta types) →
   minimal Claude loop (streaming, caching, the prefix fingerprint and TEST-02 from the start) → tool pipeline with
   approval and audit → bookshop database in Docker and the console → telemetry and the dashboard (APP-20) → sessions,
   budgets, status line → memory → compaction → MCP → session summarizer → GEN-06 samples, demo script, live smoke test.
3. Check prerequisites: Docker, the .NET 10 SDK, an Anthropic API key or `ant auth login` for live tests.
4. Decided (REQUIREMENTS §7): Q1 Npgsql, no object mapper; Q2 a small JSON Schema validator in the core; D11 the
   standalone Aspire dashboard.

## How we work

- **Every change goes through a branch and a pull request** to `main`, docs included. Never push to `main`. Merge
  only when the Opus reviewer has approved and the required checks pass (`ubuntu-latest`, `windows-latest`,
  `quality`, branch up to date, review threads resolved); the owner has delegated that go-ahead. Branches: `slice/<id>-<slug>`,
  `docs/<topic>`, `fix/<slug>`. Retarget a stacked PR before deleting the branch it is based on (deleting a base
  closes the PR).
- **The simplest thing that works.** Build only what the slice's acceptance criteria need: no abstraction without a
  current user, no setting without a known case (until then, a constant), no optimization without a measured target.
  Prefer a framework feature over custom code. Simplicity never at the cost of separation of concerns or clear design.
- **Tests replace only system boundaries:** the model provider (scripted model), network and MCP servers, the clock,
  environment and secrets, the human (scripted approver), storage back ends where a real one is impractical. Everything
  inside Officina is tested with real objects. Prefer a real database in Docker over a faked one for the application.
- **Tests are the agent's check on its own work.** Example tests say what should happen; property tests say what
  must never happen. Tests are fast and deterministic: a flaky test, or one slow enough to notice (over a second offline), is a bug to fix, never something to retry.
- **Short design docs:** tables and diagrams over prose; cite requirement IDs instead of restating them.
- **Who codes and reviews (when subagents are used):** Opus writes big or risky slices; Sonnet only small, well-bounded
  fixes. An Opus reviewer approves each PR, checking conventions, the design rules below and over-complication; at most
  3 review rounds, then stop and summarize for the owner.
- Review comments may arrive as a pending review: read them with GraphQL
  `pullRequest(number: N) { reviewThreads { … } }`, as the REST endpoints don't return them.

## Design rules that code must keep

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
- **Purpose-neutral core:** no domain concepts, UI, storage technology or transport in the core; everything except
  model and instructions is optional.
- **Dependencies:** no Microsoft Agent Framework or `Microsoft.Extensions.AI`. The Anthropic C# SDK is used only in the
  Claude package; a dependency check test enforces it.

## Claude API notes

Current models and features move fast; check the `claude-api` skill before writing provider code. Key points for
Opus 5.5 (the default model): thinking can't be disabled (set effort explicitly, its default is `medium`); forced
`tool_choice` is rejected; reasoning blocks are bound to the exact prefix that produced them, so history must stay
append-only; use the beta message types only (agentic-core spike S00b).
