# Officina — Master Plan

Slices `REQUIREMENTS.md` (revision 2) into deliverable work.

## Status

Last updated 2026-10-02.

- **Done:** every slice, S00a to S20, and S21's five parts. S21 stays `doing` only for the live TEST-31 benchmark run, its
  last acceptance criterion. `check_coverage.py` passes; `check_verification.py` lists TEST-31 and one MUST gap
  (CFG-17) as pending, and three readings for the owner. STO-01 is closed: artifacts are files on disk, and
  `operations.storage.path` moves the storage.
- **For the owner, next:**
  1. Run the live benchmark on Linux and Windows: a pilot first, then the full set, then `report.py` over both
     ([`benchmark/`](../../benchmark/README.md)). It is estimated at about $1,200 for both systems.
  2. Before relying on the Claude features (compaction, clearing tool results, task budgets, refusal fallback), record
     one live exchange of each (S21 part 3).
  3. Add the standard-user Windows sandbox tests to CI
     ([`scripts/windows-standard-user-tests.ps1`](../../scripts/windows-standard-user-tests.ps1), S21 part 5).
  4. Confirm the items [S21](S21-hardening.md) proposes as not in v1, and whether CPU throttling needs a limit-hit
     result of its own (S21 part 5).
  5. Confirm the readings of CFG-01, CAP-01 and TASK-02 ([`verification.md`](verification.md)), or change them at
     revision 3, with the other questions in REQUIREMENTS.md §13.
  6. Decide the MUST gap in [`verification.md`](verification.md): CFG-17 (`sof` does not detect the build and
     test commands). TASK-08 is built: `/board` and `/task` at the console, during a run and between a chat's replies.
     STO-01 is built: artifacts are files on disk, and `operations.storage.path` moves the storage.
     Build it, or change it at revision 3. The configuration reference §17 keeps its spec.
  7. Sign off the M0 design review of `REQUIREMENTS.md`, `CONFIGURATION.md` and `DESIGN.md`.
- **After the plan:** `sof chat` (plain `sof`), an interactive session that keeps its context: each message is a run of
  the conversation trigger in the conversation the agent keeps with the owner, the reply streams, and every command is
  typed after a `/` (TRG-01, CAP-05, HITL-01 to HITL-03, RUN-06, UX-01, LAT-02). See the user guide, section 4. At a terminal its
  lines are edited with RadLine: Tab completion and history.
- **Open follow-ups**, each waiting for a case or for the owner:
  - Chat with a single agent in the workspace: each message works in a fresh working copy, so edits that are not
    integrated are lost between messages (the session and `config validate` warn). For the owner: keep one working copy
    for the whole session, or integrate each message's changes when its reply ends.
  - Flaky test: `ModelGatewayTests.A_fallback_serves_the_call_when_the_primary_stays_unavailable_and_the_switch_is_recorded`
    listens with a process-wide `MeterListener`, so it can hear the fallbacks of `HistoryTests` running in parallel.
    Filter on a model only it uses, or run it in a non-parallel collection (a separate PR).
  - Masking: tokens are numbered per run, but history is kept across runs, so validation refuses a history strategy
    other than `none` for an agent with a `receivesMaskedValues` tool while masking is on. Keeping the token table with
    the conversation would lift that. Tokens from before a crash are not restored on resume.
  - Resume and rollback (S19): a resumed pattern runs again from its first step; integration squashes checkpoint commits,
    so older ones are kept only by the reflog and `git gc` can break restoring them; the host passes the `Caller` to
    `ResumeAsync`, as only its id and tenant are stored; a crashed run's branches are kept until it ends; nothing lists
    runs; the removal of the Windows AppContainer profile is not verified.
  - Cost and performance (S21): a cancelled turn, or one whose process died, is not charged to its task; server tools'
    per-use fees are not priced; the board tools read and rebuild the board for each change; `EventBus` holds one lock
    across each durable append, and each SQLite write opens its own connection (about 25 ms a write on Windows).
  - Configuration (S02): `formatVersion` is checked though only version 1 exists; `output.schema` is JSON text, not the
    `{ "file": … }` form; the binder appends a configured list to a non-empty default, so list settings default to unset
    and code supplies the default. A plain-text `apiKey` is refused on purpose, as the binder would skip it silently.

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
| [S20](S20-team.md) | Team | M6 | M ×3 | S13, S18, S19 | [#22](https://github.com/sleepyshark85/Officina/issues/22) | done |
| [S21](S21-hardening.md) | Hardening and benchmark | M7 | M ×5 | S20 | [#23](https://github.com/sleepyshark85/Officina/issues/23) | doing |

## Order

As planned:

- **First, in parallel:** S00a, S00b and S01.
- **Critical path:** S01 → S02 → S03 → S04 → S05 → S11 → S12 → S13 → S20 → S21.
- **Can run alongside once S04 is done:** S08, S10, S09 and S16 (once S08 is done), and S06 and S07 (once S05 is done).
- **The coding track can start early:** S14 needs only S03. S15 needs S00a and S14.

## Keeping it current

- Update a slice's **Status** (todo, doing, done) in its file and in the table above.
- If a requirement changes, update the slice that closes it, then run the coverage check.
- Each slice has a GitHub issue under its milestone. Close the issue when the slice is done.
- The SQLite format version is 5. A slice that adds a table bumps it, unless an unmerged PR has already bumped it past
  the version on main.
