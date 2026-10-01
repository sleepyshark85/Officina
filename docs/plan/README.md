# Officina — Master Plan

Status: draft · 2026-10-01 · slices `REQUIREMENTS.md` (revision 2) into deliverable work

## How slices work

- **Vertical.** Each slice ends with something that runs, with offline tests. There are no layer-only slices.
- **Traceable.** Every requirement is closed by exactly one slice, listed on its `**Closes:**` line.
  `python3 docs/plan/check_coverage.py` checks this, and it currently passes for all 257 requirements.
- **Small.** S is up to 2 days and M is 3–5 days. Anything bigger is split.
- **Risk first.** The spikes (S00a, S00b) run before the work they inform is committed.

A slice is **done** when:
- its acceptance criteria pass in CI on Linux and Windows;
- every requirement it closes has a test or a recorded review;
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
| [S00a](S00a-sandbox-spike.md) | Sandbox spike | M0 | S | — | [#1](https://github.com/sleepyshark85/Officina/issues/1) | todo |
| [S00b](S00b-claude-sdk-spike.md) | Claude SDK spike | M0 | S | — | [#2](https://github.com/sleepyshark85/Officina/issues/2) | todo |
| [S01](S01-walking-skeleton.md) | Walking skeleton | M1 | M | — | [#3](https://github.com/sleepyshark85/Officina/issues/3) | todo |
| [S02](S02-configuration.md) | Configuration | M1 | M | S01 | [#4](https://github.com/sleepyshark85/Officina/issues/4) | todo |
| [S03](S03-tool-pipeline.md) | Tool pipeline | M2 | M | S02 | [#5](https://github.com/sleepyshark85/Officina/issues/5) | todo |
| [S04](S04-turn-loop.md) | Turn loop | M2 | M | S03 | [#6](https://github.com/sleepyshark85/Officina/issues/6) | todo |
| [S05](S05-context-and-caching.md) | Context and caching | M2 | M | S04 | [#7](https://github.com/sleepyshark85/Officina/issues/7) | todo |
| [S06](S06-run-record-and-output.md) | Run record and output | M2 | M | S04 | [#8](https://github.com/sleepyshark85/Officina/issues/8) | todo |
| [S07](S07-history-and-conversations.md) | History and conversation store | M2 | S | S05 | [#9](https://github.com/sleepyshark85/Officina/issues/9) | todo |
| [S08](S08-events-storage-observability.md) | Events, storage and observability | M2 | M | S04 | [#10](https://github.com/sleepyshark85/Officina/issues/10) | todo |
| [S09](S09-triggers-and-admission.md) | Triggers and admission | M2 | M | S04, S08 | [#11](https://github.com/sleepyshark85/Officina/issues/11) | todo |
| [S10](S10-mcp-and-knowledge.md) | MCP and knowledge sources | M2 | M | S03 | [#12](https://github.com/sleepyshark85/Officina/issues/12) | todo |
| [S11](S11-claude-provider.md) | Claude provider | M3 | M | S00b, S05 | [#13](https://github.com/sleepyshark85/Officina/issues/13) | todo |
| [S12](S12-model-gateway.md) | Model gateway | M3 | S | S11 | [#14](https://github.com/sleepyshark85/Officina/issues/14) | todo |
| [S13](S13-loop-patterns.md) | Loop patterns | M4 | M | S04, S06 | [#15](https://github.com/sleepyshark85/Officina/issues/15) | todo |
| [S14](S14-git-workspace.md) | Git workspace | M5 | M | S03 | [#16](https://github.com/sleepyshark85/Officina/issues/16) | todo |
| [S15](S15-sandbox.md) | Sandbox | M5 | M ×2 | S00a, S14 | [#17](https://github.com/sleepyshark85/Officina/issues/17) | todo |
| [S16](S16-human-interaction-cli.md) | Human interaction and CLI | M5 | M | S04, S08 | [#18](https://github.com/sleepyshark85/Officina/issues/18) | todo |
| [S17](S17-project-memory.md) | Project memory | M5 | S | S05, S16 | [#19](https://github.com/sleepyshark85/Officina/issues/19) | todo |
| [S18](S18-task-board.md) | Task board | M6 | M | S06, S08 | [#20](https://github.com/sleepyshark85/Officina/issues/20) | todo |
| [S19](S19-checkpoints-long-runs.md) | Checkpoints and long runs | M6 | M | S08, S14 | [#21](https://github.com/sleepyshark85/Officina/issues/21) | todo |
| [S20](S20-team.md) | Team | M6 | M | S13, S18, S19 | [#22](https://github.com/sleepyshark85/Officina/issues/22) | todo |
| [S21](S21-hardening.md) | Hardening and benchmark | M7 | M | S20 | [#23](https://github.com/sleepyshark85/Officina/issues/23) | todo |

## Order

- **Now, in parallel:** S00a, S00b and S01.
- **Critical path:** S01 → S02 → S03 → S04 → S05 → S11 → S12 → S13 → S20 → S21.
- **Can run alongside once S04 is done:** S06, S07, S08, S09, S10, and S16 (once S08 is done).
- **The coding track can start early:** S14 needs only S03. S15 needs S00a and S14.

## Keeping it current

- Update a slice's **Status** (todo, doing, done) in its file and in the table above.
- If a requirement changes, update the slice that closes it, then run the coverage check.
- Each slice has a GitHub issue under its milestone. Close the issue when the slice is done.
