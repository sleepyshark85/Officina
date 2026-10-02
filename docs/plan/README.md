# Officina — Master Plan

Status: draft · 2026-10-01 · slices `REQUIREMENTS.md` (revision 2) into deliverable work

## Status

Last updated 2026-10-02.

- **Done:** M0 spikes S00a and S00b; M1 slices S01 (walking skeleton) and S02 (configuration); S03 (tool pipeline); S04 (turn loop); S05 (context and caching); S06 (run record and output); S07 (history and conversation store); S08 (events, storage and observability); S09 (triggers and admission); S10 (MCP and knowledge); S11 (Claude provider); S12 (model gateway); S13 (loop patterns); S14 (git workspace); S15 (sandbox); S16 (human interaction and CLI); S17 (project memory); S18 (task board); S19 (checkpoints and long runs: part 1, checkpoints, resume and rollback; part 2, budgets, report and cleanup).
- **Waiting:** the M0 design review sign-off on `REQUIREMENTS.md`, `CONFIGURATION.md` and `DESIGN.md`.
- **Doing:** S20 (team): part 1, the team runs ([#22](https://github.com/sleepyshark85/Officina/issues/22)); next part 2,
  integration and the scripted simulation, then part 3, definitions, presets, helpers and sign-offs.
- **Next:** S20 part 2.
- **Open follow-ups:**
  - S02 kept a `formatVersion` check, though only version 1 exists. Consider removing the setting
    until a version 2 exists (principle 13). S06 keeps `output.schema` as JSON text, and S13 too; if a later slice wants the
    reference's `{ "file": … }` form, that is a format change.
  - S02 rejects a plain-text `apiKey`, because the binder would otherwise skip it silently and
    fall back to the default secret reference. Kept on purpose.
  - S21 (from S13 and S20): the condition roots `checks.<name>`, `outcome` and `stopReason` (configuration reference §6, marked
    not built) wait for a case that needs them; the team needed none.
  - The configuration binder adds a file's items to a list setting's default instead of replacing it, so
    `storage.unstoredEvents` set in a file keeps `textGenerated`. S06 made `context.record` unset by default to avoid it.
  - S21 makes the Claude provider an `IHistoryShortener` (CLD-06); the capability slices (S19, S20) add their switches and
    dependencies to `CapabilitiesOptions`. S07's Notes list the rest.
  - S04 left parts of its requirements to the slices that add the state they need; its Notes list them
    (S06, S07, S09, S14, S16, S19).
  - S08 left parts of its requirements to the slices that add the state they need; its Notes list them
    (S06, S07, S09, S16, S19, S20); S13 added steps.
  - S11 moved CLD-06 and CLD-11 (the Claude provider's feature switches and Message Batches) to S21. Its Notes list the rest.
  - S21 (from S12): an operator's message sent during a turn, and the project-memory change message (S17, sent
    automatically whenever memory changes), is a mid-conversation system message, which every Claude model outside the
    allow list (such as Haiku 4.5 and Sonnet 5) rejects (`InvalidRequest`); the provider should send it as a user message for them. A reply the model
    gateway starts over after a mid-stream failure leaves its first text in the stored `textGenerated` events, and retries
    are not events of their own. The `baseUrl` provider setting of the reference is not built.
  - S20 part 2 (from S16): `sof run` gives each agent one working copy, named `<run>-<agent>` (an agent of a team by its id),
    opened when the agent first calls a `workspace.*` or `sandbox.*` tool and disposed when the run ends. Part 2 gives each
    task its own and disposes it when the task ends. Nothing integrates in `sof run` before then, so the CLI shows an empty
    integration queue, and a team cannot be on with the workspace.
  - S20 part 2 (from S16, S18 and S06): the CLI's board view (TASK-08); the command checks integration needs and the
    `capabilities.workspace.baselineChecks` setting that names them (WS-02). Fan-out branches of one agent share that
    agent's working copy while they run at the same time; part 2 gives branches their own when they change files.
  - S21 (from S16): `sof config dry-run` cannot run a configuration that uses the `workspace.*` or `sandbox.*` tools. It
    should register them over the test kit's `InMemoryWorkspace` and `FakeSandbox` (CFG-12).
  - S20 part 3 adds the plan-approval sign-off and the model's tool to hand off to a human (EGR-04). Part 2 adds integration
    to `IWorkspace`, and takes a checkpoint at `CheckpointPoint.Integration` after each one.
  - S21 (from S16): when a cancelled turn outlives `run.cancelWithin`, the agent's lock is released, so its next turn
    can overlap with the left-behind one, and the left-behind turn reports zero cost.
  - S20 part 1 (from S19): a team's agents share the run's budget in one process, each with its own agent level from its
    definition's `budget.total`; the run's time is still its elapsed time inside work items, which is what RUN-05 means. Events
    carry the agent's id, so cost is broken down by agent and by definition (RUN-10). A resumed run's agent level counts all its
    events. The run's tokens and tool-call limits (RUN-05) stay with the turn and the agent, and the task has cost only.
    S21 (from S19 and S20): the per-run rate limit (ING-03) counts a run's first work and each resume, in memory, and a team's
    tasks are its own work, so nothing joins a live run from outside; the CLI resumes in a new process, so the limit never applies
    there, and the host and serve mode keep it in a long-lived runner. `budget.total` of a pattern's step agents does not apply
    (they draw on the entry agent's).
  - S21 runs the Windows sandbox tests once as a standard user in CI (the S00a recipe), tests the CPU
    limit on both systems with limit reporting, and proves HTTPS through the proxy (a CONNECT tunnel).
  - S20 part 1 runs the team on the task board: it claims and assigns tasks, a failed task goes back to the lead, who
    retries it, and a verified, approved task is done. Part 2 gives each task a working copy that its checks look at,
    integrates a task in review once verified and approved, then calls `CompleteAsync`, or `ReturnAsync` on a conflict or
    failed baseline check (WS-03, TASK-05). S18's Notes list the rest.
  - S18 moved the SQLite format version to 3. A slice that adds a table bumps it again, unless an unmerged PR has
    already bumped it past the version on main.
  - S19 (follow-up): a resumed pattern runs again from its first step, redoing the steps already done (resume part-way);
    old checkpoint commits are kept only by the reflog after a squash, so `git gc` can break restoring them. The Windows
    sandbox cleanup is proved on CI to remove the home folder and the container's grants on the toolchains; the AppContainer profile's
    removal is not verified. Open, for a case (S19's Notes list them):
    masking tokens from before a crash are not restored; the host passes the `Caller` to `ResumeAsync`, because only its id and
    tenant are stored; a crashed run's branches are kept until it ends, and nothing lists runs yet.
  - S05 moved CTX-06 to S07 and CTX-07 to S06.
  - S17 left the owner's CLI commands for memory proposals (S20 part 2; part 1 tied `memory.review` to the team's lead),
    and unmasking proposed text (S21); their slice files say so.
  - S09 left parts of its requirements to the slices that add the state they need; its Notes list them
    (S06, S19, S20, and S21 for Message Batches).
  - REQUIREMENTS.md §4.3 lists masking as replaceable, but DESIGN.md §4 keeps it in Core and not replaceable in v1.
    The owner decides at revision 3.
  - Masking tokens are numbered per run, but S07 keeps history across runs, so a token in earlier history could
    name a different value in a later run. Until the token table is kept with the conversation, validation refuses
    a history strategy other than `none` for an agent with a `receivesMaskedValues` tool while masking is on.
    Keeping the table with the conversation lifts that restriction.
  - The configuration binder appends a configured list to a non-empty default list, so list settings default to
    unset and code supplies the default (S09 fixed `storage.unstoredEvents`). Dictionaries merge into their defaults on
    purpose: `providers.claude` and `models.default` rely on it.

## How slices work

- **Vertical.** Each slice ends with something that runs, with offline tests. There are no layer-only slices.
- **Traceable.** Every requirement is closed by exactly one slice, listed on its `**Closes:**` line.
  `python3 docs/plan/check_coverage.py` checks this, and it currently passes for all 257 requirements.
- **Small.** S is up to 2 days and M is 3–5 days. Anything bigger is split.
- **Risk first.** The spikes (S00a, S00b) run before the work they inform is committed.

A slice is **done** when:
- its acceptance criteria pass in CI on Linux and Windows;
- every requirement it closes has a test or a recorded review;
- its tests replace only system boundaries, never internal types (DESIGN.md §11);
- it builds nothing its acceptance criteria don't need: no unused abstractions, no settings without a
  known case, no optimization without a measured target (principle 13);
- its settings appear in the generated reference.

## Milestones

| Milestone | Slices | Demo at the end |
|---|---|---|
| M0 Design review | S00a, S00b | Spike findings feed the review; specification, configuration and design approved |
| M1 Foundations | S01, S02 | A configured agent runs against a scripted model; `sof config show --origin` works |
| M2 Single agent | S03–S10 | A tool-using agent runs offline with gates, budgets, caching layout, record, events and storage |
| M3 Claude integration | S11, S12 | The same agent runs live on Claude, with cache hits and fallback |
| M4 Loop patterns | S13 | Every pattern runs from its sample configuration |
| M5 Coding agent | S14–S17 | One agent changes, builds and tests a real project from `sof`, sandboxed |
| M6 Team and long-running | S18–S20 | The scripted team simulation passes, including a forced restart |
| M7 Hardening | S21 | Load targets met; the coding team benchmark reaches 90% |

## Slices

| Slice | Title | Milestone | Size | Depends on | Issue | Status |
|---|---|---|---|---|---|---|
| [S00a](S00a-sandbox-spike.md) | Sandbox spike | M0 | S | — | [#1](https://github.com/sleepyshark85/Officina/issues/1) | done |
| [S00b](S00b-claude-sdk-spike.md) | Claude SDK spike | M0 | S | — | [#2](https://github.com/sleepyshark85/Officina/issues/2) | done |
| [S01](S01-walking-skeleton.md) | Walking skeleton | M1 | M | — | [#3](https://github.com/sleepyshark85/Officina/issues/3) | done |
| [S02](S02-configuration.md) | Configuration | M1 | M | S01 | [#4](https://github.com/sleepyshark85/Officina/issues/4) | done |
| [S03](S03-tool-pipeline.md) | Tool pipeline | M2 | M | S02 | [#5](https://github.com/sleepyshark85/Officina/issues/5) | done |
| [S04](S04-turn-loop.md) | Turn loop | M2 | M | S03 | [#6](https://github.com/sleepyshark85/Officina/issues/6) | done |
| [S05](S05-context-and-caching.md) | Context and caching | M2 | M | S04 | [#7](https://github.com/sleepyshark85/Officina/issues/7) | done |
| [S06](S06-run-record-and-output.md) | Run record and output | M2 | M | S04, S05 | [#8](https://github.com/sleepyshark85/Officina/issues/8) | done |
| [S07](S07-history-and-conversations.md) | History and conversation store | M2 | S | S05 | [#9](https://github.com/sleepyshark85/Officina/issues/9) | done |
| [S08](S08-events-storage-observability.md) | Events, storage and observability | M2 | M | S04 | [#10](https://github.com/sleepyshark85/Officina/issues/10) | done |
| [S09](S09-triggers-and-admission.md) | Triggers and admission | M2 | M | S04, S08 | [#11](https://github.com/sleepyshark85/Officina/issues/11) | done |
| [S10](S10-mcp-and-knowledge.md) | MCP and knowledge sources | M2 | M | S03 | [#12](https://github.com/sleepyshark85/Officina/issues/12) | done |
| [S11](S11-claude-provider.md) | Claude provider | M3 | M | S00b, S05 | [#13](https://github.com/sleepyshark85/Officina/issues/13) | done |
| [S12](S12-model-gateway.md) | Model gateway | M3 | S | S11 | [#14](https://github.com/sleepyshark85/Officina/issues/14) | done |
| [S13](S13-loop-patterns.md) | Loop patterns | M4 | M | S04, S06 | [#15](https://github.com/sleepyshark85/Officina/issues/15) | done |
| [S14](S14-git-workspace.md) | Git workspace | M5 | M | S03 | [#16](https://github.com/sleepyshark85/Officina/issues/16) | done |
| [S15](S15-sandbox.md) | Sandbox | M5 | M ×2 | S00a, S14 | [#17](https://github.com/sleepyshark85/Officina/issues/17) | done |
| [S16](S16-human-interaction-cli.md) | Human interaction and CLI | M5 | M | S04, S08 | [#18](https://github.com/sleepyshark85/Officina/issues/18) | done |
| [S17](S17-project-memory.md) | Project memory | M5 | S | S05, S16 | [#19](https://github.com/sleepyshark85/Officina/issues/19) | done |
| [S18](S18-task-board.md) | Task board | M6 | M | S06, S08 | [#20](https://github.com/sleepyshark85/Officina/issues/20) | done |
| [S19](S19-checkpoints-long-runs.md) | Checkpoints and long runs | M6 | M ×2 | S08, S14 | [#21](https://github.com/sleepyshark85/Officina/issues/21) | done |
| [S20](S20-team.md) | Team | M6 | M ×3 | S13, S18, S19 | [#22](https://github.com/sleepyshark85/Officina/issues/22) | doing |
| [S21](S21-hardening.md) | Hardening and benchmark | M7 | M | S20 | [#23](https://github.com/sleepyshark85/Officina/issues/23) | todo |

## Order

- **Now, in parallel:** S00a, S00b and S01.
- **Critical path:** S01 → S02 → S03 → S04 → S05 → S11 → S12 → S13 → S20 → S21.
- **Can run alongside once S04 is done:** S08, S10, S09 and S16 (once S08 is done), and S06 and S07 (once S05 is done).
- **The coding track can start early:** S14 needs only S03. S15 needs S00a and S14.

## Keeping it current

- Update a slice's **Status** (todo, doing, done) in its file and in the table above.
- If a requirement changes, update the slice that closes it, then run the coverage check.
- Each slice has a GitHub issue under its milestone. Close the issue when the slice is done.
