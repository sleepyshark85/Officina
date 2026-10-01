# Officina — Master Plan

Status: draft · 2026-10-01 · slices `REQUIREMENTS.md` (revision 2) into deliverable work

## Status

Last updated 2026-10-02.

- **Done:** M0 spikes S00a and S00b; M1 slices S01 (walking skeleton) and S02 (configuration); S03 (tool pipeline); S04 (turn loop); S05 (context and caching); S06 (run record and output); S07 (history and conversation store); S08 (events, storage and observability); S09 (triggers and admission); S10 (MCP and knowledge); S14 (git workspace); S15 (sandbox).
- **Waiting:** the M0 design review sign-off on `REQUIREMENTS.md`, `CONFIGURATION.md` and `DESIGN.md`.
- **Next:** S16.
- **Open follow-ups:**
  - S02 kept a `formatVersion` check, though only version 1 exists. Consider removing the setting
    until a version 2 exists (principle 13). S06 keeps `output.schema` as JSON text; when S13 or S16 want the
    reference's `{ "file": … }` form, that is a format change.
  - S02 rejects a plain-text `apiKey`, because the binder would otherwise skip it silently and
    fall back to the default secret reference. Kept on purpose.
  - S18 adds the task board to `GateContext` (TOOL-06), and the `task` record scope (REC-06).
  - S13 adds the output-check failure outcome "revise" where the pattern supports it (OUT-03); until then a failed check
    hands off.
  - The configuration binder adds a file's items to a list setting's default instead of replacing it, so
    `storage.unstoredEvents` set in a file keeps `textGenerated`. S06 made `context.record` unset by default to avoid it.
  - S11 maps provider tools and their limits, such as maximum uses, to the Claude request.
  - S11: secrets declared in configuration, such as the provider API key and the tool servers' `env` and
    `headers`, must join the tool pipeline's redaction set when they are resolved, so they are removed like
    secrets tools read (INV-06).
  - S16 connects the tool servers in `sof run`, and offers the workspace and sandbox tools only when their
    capability is on (CAP-02).
  - S07 left the run record (S06), memory (S17) and tasks (S18) to join the shortening test (HIST-03, TEST-15);
    S11 makes the Claude provider an `IHistoryShortener`; the capability slices (S16–S20) add their switches and
    dependencies to `CapabilitiesOptions`. Its Notes list the rest.
  - S16 adds the approval timeout and the permission modes (HITL-01, HITL-02). A denied or timed-out approval
    should then hand off as "approval denied or timed out"; S04 counts it as a refusal towards a policy gap.
  - S04 left parts of its requirements to the slices that add the state they need; its Notes list them
    (S06, S07, S09, S14, S16, S18, S19).
  - S08 left parts of its requirements to the slices that add the state they need; its Notes list them
    (S06, S07, S09, S11, S13, S16–S20).
  - S11 ships the price table, requires a price when a cost budget is set, and splits `prices.*.cacheWrite`
    by cache lifetime (MDL-09). Until then a model without a configured price costs nothing.
  - S16: `capabilities.workspace.baselineChecks` names checks from the `checks` section (added by S06) for
    `GitWorkspace` to run, which the host passes in code today, with the command checks it needs (WS-02).
  - S16 offers the `workspace.*` tools over `WorkingCopy` (read, search, edit, write; delete and
    move with them) through `IWorkspace` in Core, with the test kit's in-memory workspace (TEST-01,
    moved from S15: the sandbox needs only the working copy's folder). It gives each agent its working
    copy and its `SandboxTools`, disposes them when the agent, task or run ends (SBX-03), wires the
    sandbox tools and the command rules gate as built-ins, and shows the integration queue (WS-09).
    It probes the sandbox once at startup (SBX-07), rather than once per agent's `SandboxTools`.
  - S19 cleans up what the Windows sandbox leaves outside a cleaned-up working copy: its AppContainer
    profile, its home folder `%TEMP%\officina-<hash>`, and its read-and-execute grants on `toolchains` folders.
  - S21 runs the Windows sandbox tests once as a standard user in CI (the S00a recipe), tests the CPU
    limit on both systems with limit reporting, and proves HTTPS through the proxy (a CONNECT tunnel).
  - S18 turns an integration conflict or failed check into work for the author or the lead (WS-03),
    and integrates a task only after its verification checks pass (TASK-05).
  - S19 snapshots and restores the workspace, and cleans up worktrees left by a crash.
  - S05 moved CTX-06 to S07 and CTX-07 to S06, and left boundary ② and the memory-change case of TEST-09 to S17;
    its Notes list the rest (S06, S09, S18).
  - S11 maps cache boundaries to `cache_control`, putting boundary ③ on the last cacheable block before a
    turn-scoped message; maps `Role.System` to mid-conversation system messages and `TurnScoped` to `clear_at`; and
    reports `CacheBoundaries` and `TurnScopedMessages` in its capabilities.
  - S09 left parts of its requirements to the slices that add the state they need; its Notes list them
    (S06, S11, S19, S20).
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
| [S11](S11-claude-provider.md) | Claude provider | M3 | M | S00b, S05 | [#13](https://github.com/sleepyshark85/Officina/issues/13) | todo |
| [S12](S12-model-gateway.md) | Model gateway | M3 | S | S11 | [#14](https://github.com/sleepyshark85/Officina/issues/14) | todo |
| [S13](S13-loop-patterns.md) | Loop patterns | M4 | M | S04, S06 | [#15](https://github.com/sleepyshark85/Officina/issues/15) | todo |
| [S14](S14-git-workspace.md) | Git workspace | M5 | M | S03 | [#16](https://github.com/sleepyshark85/Officina/issues/16) | done |
| [S15](S15-sandbox.md) | Sandbox | M5 | M ×2 | S00a, S14 | [#17](https://github.com/sleepyshark85/Officina/issues/17) | done |
| [S16](S16-human-interaction-cli.md) | Human interaction and CLI | M5 | M | S04, S08 | [#18](https://github.com/sleepyshark85/Officina/issues/18) | todo |
| [S17](S17-project-memory.md) | Project memory | M5 | S | S05, S16 | [#19](https://github.com/sleepyshark85/Officina/issues/19) | todo |
| [S18](S18-task-board.md) | Task board | M6 | M | S06, S08 | [#20](https://github.com/sleepyshark85/Officina/issues/20) | todo |
| [S19](S19-checkpoints-long-runs.md) | Checkpoints and long runs | M6 | M | S08, S14 | [#21](https://github.com/sleepyshark85/Officina/issues/21) | todo |
| [S20](S20-team.md) | Team | M6 | M | S13, S18, S19 | [#22](https://github.com/sleepyshark85/Officina/issues/22) | todo |
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
