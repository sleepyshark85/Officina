# S21 — Hardening and benchmark

**Milestone:** M7 · **Size:** M ×5 · **Depends on:** S20 · **Issue:** [#23](https://github.com/sleepyshark85/Officina/issues/23) · **Status:** doing

## Goal

Prove the non-functional targets and the coding team's success rate.

**Closes:** CLD-06, CLD-11, SCALE-01, SCALE-02, SCALE-03, LAT-01, LAT-02, SEC-03, TEST-30, TEST-31, TEST-33, HITL-07

## Acceptance criteria

- [x] Automated load tests measure SCALE-02, LAT-01 and LAT-02, and the targets are met.
- [ ] The coding team benchmark reaches at least 90% across all runs, on Linux and Windows.
- [x] Coverage of the core is at least 85%.
- [ ] Every MUST is verified by a test or a recorded review (v1 acceptance).

The benchmark runs against the live model, which costs money, so the owner starts it (part 1 says how). S21 stays `doing` until
its runs reach 90% on both systems.

## Parts

| Part | What | Status |
|---|---|---|
| 1 | The benchmark up to the live run: runner, scoring, report, reference solutions and the suites' validation (TEST-31) | done |
| 2 | Load and latency tests (TEST-30: SCALE-01, SCALE-02, LAT-01, LAT-02, storage writes), SCALE-03, coverage (TEST-33), the MUST verification check | done |
| 3 | The Claude provider's feature switches and compaction (CLD-06), and the S12 follow-ups | done |
| 4 | The core follow-ups: a task's tokens, time and tool calls (RUN-05), step agents' `budget.total`, `config validate` matching `sof run`, `config dry-run` with the workspace and sandbox tools, masking tokens in memory proposals, a cancelled turn that outlives `run.cancelWithin` | done |
| 5 | The sandbox follow-ups: the CPU limit with limit reporting on both systems, HTTPS through the proxy, and the Windows tests as a standard user | todo |

## The follow-ups S21 took

Every item the plan or a slice moved to S21, and where it goes. "Not in v1" items are proposed for the owner to confirm; each
has a reason under principle 13.

| From | Item | Decision |
|---|---|---|
| S20 part 3 | Run the TEST-31 benchmark against the live model, with each hidden suite validated first | Part 1 builds everything up to the live run; the owner runs it |
| S11, S06, S07 | CLD-06: native structured output, compaction as the provider's `IHistoryShortener`, clearing old tool results, task budgets, the refusal fallback | Built in part 3 |
| S11, S09 | CLD-11 (SHOULD): `ModelRequest.Batch` through Message Batches (MDL-10) | Not in v1: `sof`, the only v1 application, has no batch trigger. A batch call can wait up to a day, so it needs a way to hold no place in the model gateway's line meanwhile and to be priced at batch rates; both are design work for the first application that batches |
| S12 | Operator and memory-change messages sent as a user message to models outside the allow list; a restarted reply's first text left in `textGenerated`, and retries that are not events | Built in part 3 |
| S12 | The `baseUrl` provider setting of the configuration reference | Not in v1: `sof` calls the Claude API itself, and no case needs another address; the reference now says it is not built |
| S20 part 1 | RUN-05: a task's tokens, time and tool calls (its budget caps its cost only) | Built in part 4 |
| S20 part 1 | `budget.total` of a pattern's step agents, which draw on the entry agent's level | Built in part 4 |
| S20 part 2 | `sof config validate` reports the `extension:` tools, gates and checks `sof` never registers, as `sof run` refuses them | Built in part 4, with the provider checks too |
| S16 | `sof config dry-run` with the `workspace.*` and `sandbox.*` tools over the test kit's `InMemoryWorkspace` and `FakeSandbox` (CFG-12) | Built in part 4 |
| S17 | Masking tokens in text proposed for project memory: restore them or refuse the proposal | Built in part 4: refused |
| S16 | A cancelled turn that outlives `run.cancelWithin` releases the agent's lock, so a next turn can overlap it, and reports zero cost | Built in part 4 |
| S15 | The CPU limit tested on both systems with limit reporting; HTTPS through the proxy (a CONNECT tunnel) | Part 5 |
| S15 | The Windows sandbox tests once as a standard user in CI (the S00a recipe) | Part 5 prepares it; the owner changes CI |
| S20 part 1, S19 | ING-03's per-run rate limit kept across processes by a long-lived runner, with the host and serve mode | Not in v1: v1 has only the CLI, which resumes in a new process, and a team's tasks are its own work, so nothing joins a live run from outside. It comes with the host |
| S13, S20 | The condition roots `checks.<name>`, `outcome` and `stopReason` (configuration reference §6) | Not in v1: no pattern, preset or sample needs them; the reference marks them not built |
| S20 part 2 | A working copy of their own for fan-out branches of one agent that change files | Not in v1: no case decides what becomes of each branch's changes; the samples' branches only read |
| S20 part 3 | `team.handoff` | Not in v1: a team agent's handoff goes back to its lead, and `human.request_handoff` reaches the owner |
| S16 | HITL-07 (MAY): end users rate a result, linked to its run | Not in v1: `sof` has one user, the owner, who reads the run's report |
| — | SEC-03 (MAY): a tamper-evident audit log | Not in v1: the audit log is local to the owner's machine, the only one who could change it |

Not S21's: the S19 items open "for a case" (masking tokens before a crash, resuming a pattern part-way, the `Caller` on resume,
listing runs) and the S02 notes stay in the plan's follow-ups.

## Part 1: the benchmark up to the live run

- TEST-31: [`benchmark/bench.py`](../../benchmark/bench.py) runs each goal from a new repository outside this one, with only
  `sof.json` in it, so the team never sees the hidden tests or the references. It approves each sign-off and counts it as a human
  input, declines any other request at once (as nobody answering would by `run.approvalTimeout`) and counts it, kills `sof` after a
  random model call (2 to 8, 16 or 24, by tier) and resumes the run, reads the cost and time from `sof report`, then clones the
  baseline and scores it. `--max-cost` (60 dollars) stops a run, which then fails. A run succeeds when it was restarted and its
  baseline passes its own build and tests and every hidden test. [`report.py`](../../benchmark/report.py) sums the results by goal,
  tier and system, and checks 90% with at least 3 restarted runs of each goal on each system.
- [`benchmark/reference/`](../../benchmark/reference) holds a solution for each goal, written from its paragraph alone, and
  [`validate.py`](../../benchmark/validate.py) scores each as a run's baseline in an empty folder, so each suite can be passed, and
  checks, as a sanity check only, that every suite fails against an empty workspace. All ten pass on Linux. A CI job runs it on Linux and Windows, with the runner's own tests
  against a stand-in for `sof` (`benchmark/tests/`), which restarts a run, answers its sign-off and declines its command.
- Validating found a harness bug: Python's text mode turns each newline sent to a program's standard input into CRLF on Windows,
  which `wc`'s byte count and the CSV's quoted line break would see. The harness now sends bytes. No test changed what it checks.
- The live run, which costs money and needs the owner: `ANTHROPIC_API_KEY=… python3 benchmark/bench.py` on Linux and on Windows,
  after a pilot (`--goals s1-word-count --runs 1`), then `report.py` over both results folders. At the shipped prices of
  `claude-opus-5-5` the estimate is $5 a small run, $20 a medium one and $50 a larger one: about $600 a system, $1,200 for both,
  and it may be half or twice that.
- From the review of the protected-files fix: `protectedPaths` says to use `dir/**` for a folder's contents, as `dir` alone
  matches only the folder's own path in the file tools and at integration.

## Part 2: load, latency, coverage and verification

`tests/Sleepyshark.Officina.Load.Tests` runs the real runner against the scripted model, one test at a time; CI runs it in a
step of its own after the other tests, and it prints what it measured. Measured on the development machine, and on CI's
Linux and Windows runners where they differ much:

| Requirement | Test | Target | Measured |
|---|---|---|---|
| LAT-01 | 50 turns of 20 model calls, each asking for a tool that reads or one that writes behind a gate, storage in memory; the time inside the model provider taken out of each iteration | p95 < 5 ms | p95 0.15 ms, p99 0.22 ms |
| LAT-02 | 100 replies whose first text is timed from the provider handing it over to the caller reading the run's events, on SQLite. Streamed text is not stored by default, so no durable write is on its path: one interactive agent with nothing else writing | < 50 ms (p95 and p99 asserted) | p95 0.06 ms |
| TEST-30, storage | The LAT-01 turns on SQLite with the conversation store on; each write timed | reported | a write (an event, an audit entry or a conversation turn), p50: 5.7 ms here, 1.4 ms on CI's Linux, 25 ms on CI's Windows; an iteration with its writes, p50: 20 ms, 5.6 ms, 100 ms |
| SCALE-02 | One team run: the lead plans 500 tasks, 8 developers take them, the first 8 held until all 8 work at once; storage in memory. A second test runs the same 8 at once on SQLite, with 16 tasks | 8 at once, 500 done | 8 at once, 500 done in 9 s (18 ms a task, 7,031 events); on SQLite, 16 tasks in 4.6 s. The first version's 500 tasks on SQLite took 87 s here, 82 s on CI's Linux, 331 s on CI's Windows |
| SCALE-03 | The same run keeps its pace: the last 100 tasks take at most half as long again as the second 100, plus 0.5 s, so a pause of the machine's of that order does not fail it | steady | 1.13 s against 1.11 s; on SQLite the pace varied with the disk by up to 60% on CI, with no trend in the core |
| SCALE-01 | 1,000 callers' conversations with one agent, started at once, then each a second turn whose history is restored from the conversation store | all complete, restored | 2,000 turns in 0.7 s, in memory |

- What the numbers say: the core's own time per iteration is far below LAT-01's target, which leaves out tool time. A team run's
  time is mostly elsewhere: in memory, a task still costs 14 to 18 ms here and 40 to 65 ms on CI, almost all of it inside the
  built-in board tools (`tasks.create` p50 6 ms, `tasks.submit_for_review` p50 11 ms and p95 55 to 71 ms, as the #60 review measured;
  model time was 0.05 s of 8.7 s). On SQLite its durable writes come on top. Each write opens a
  connection of its own (pooling is off, so nothing keeps the file open) and syncs, about 6 ms here, of which about 1 ms is the
  sync: a held connection would cut a write to about a quarter. On Windows a write takes 25 ms, so a run of 500 tasks spends five
  and a half minutes in storage, against hours of model calls. No requirement sets a target for it, so it is reported, not
  changed (principle 13); the owner may set one if `sof`'s overhead on Windows matters. The scale tests use storage in memory, so
  they measure the core, are steady on CI, and take seconds; the first version ran SCALE-02 on SQLite, and on CI its pace check
  failed once on the disk's variance alone.
- Follow-ups after v1, with no target set: the board tools' cost per call (the board is read and rebuilt from its change log for
  each change); and `EventBus.PublishAsync`, which holds one runner-wide lock across each durable append, so in a team run on SQLite
  one agent's event, a streamed text delta included, waits behind the others' writes. At about 21 ms a write on Windows, the first
  text of an agent in a busy team there could take more than LAT-02's 50 ms, and the lock is why 500 tasks on SQLite took 331 s.
- SCALE-01 is a SHOULD for the document Q&A application after v1. Turns of one agent run one at a time (LOOP-02), so its
  conversations are served in turn; serving them at once is that application's work. SCALE-03's 48 hours are not run: the test
  measures the pace over a long run's worth of work, 1,000 model calls and 7,000 events, and the heap after it.
- TEST-33: `dotnet test --collect "Code Coverage;Format=cobertura" --results-directory <folder>`, then `python3
  tests/core_coverage.py <folder>`, counts a line of `src/Sleepyshark.Officina.Core` as covered when any test assembly ran it:
  96.5% of 3,758 lines, as CI's Release build measures it. CI checks it on Linux, against a threshold of 85%.
- v1 acceptance: [`check_verification.py`](check_verification.py) finds each MUST in a test or in the reviews of
  [`verification.md`](verification.md). 17 MUSTs had tests that did not name them, now named, and the load tests name theirs; three are
  recorded reviews (CFG-10, EGR-01, TASK-01). Three are readings for the owner to confirm, not verified: CFG-01 (policies and
  capabilities are the configuration's, not the agent definition's), CAP-01 (capabilities switch per application only) and TASK-02
  (the status changes are fixed, not configured). Three are pending: TEST-31's live run, CLD-06 (part 3) and SBX-01's CPU limit (part 5).

## Part 3: the Claude provider's features

- CLD-06: `providers.<name>.features` (`ProviderFeatures`), each off until switched on: `structuredOutput` sends the agent's
  `output.schema` as `output_config.format` (`ModelRequest.OutputSchema` carries it; the core still checks the output);
  `clearToolResults` sends context editing's `clear_tool_uses_20250919`; `taskBudget` sends `output_config.task_budget`;
  `refusalFallback` sends `fallbacks: "default"`. Each beta feature sends its header, and a recording now checks the header too.
  `ProviderCapabilities.Features` names what each model has, and a feature switched on for a model without it is a configuration
  error at start-up (MDL-06).
- A refusal fallback: the `fallback` block stays where it came; what the declining model produced before it is not sent back but
  its text and its server tool calls with their results, as the API asks; its tool calls are withdrawn (`ToolCallsWithdrawn`), so they
  are never run; each
  attempt in `usage.iterations` is priced by its own model, the switch reported as a fallback (`ModelFailure.Refused`), so the
  `modelFallback` event and the cost by model show it, and a later call that sticky routing sends straight to the fallback model
  is priced right too. The fallback targets Opus 4.8 and Sonnet 5 join the shipped prices.
- HIST-01: a model that `Summarizes` (`ProviderCapabilities`) is the provider's own shortening: the turn sends a request with
  `Summarize` over the earlier turns (`ModelRequest.TurnStart` says where the current turn starts) through the model gateway, so the
  call has its concurrency limit, retries and fallbacks, and is spent, priced and stored as a `modelCallEnded`, also when it fails or
  gives no summary (COST-02). Claude's is compaction on demand (beta `compact-2026-09-04`), with the same model, instructions and tools;
  the streamed block (`compaction_delta` pieces of its summary and opaque content, then its signature) takes the earlier turns' place
  as an assistant message of its own, first, as documented. The operator's messages and memory changes it summarized are told again
  after it, as the API says they stop applying. Haiku has no compaction, so an agent that has the provider shorten its history on
  Haiku is a configuration error at start-up. A fallback model without a price is warned of. `IHistoryShortener` returns a
  `ShortenedHistory` with what its own model call used, which the turn spends the same way.
- S12: a model without mid-conversation system messages gets the operator's (and memory changes) as a user message starting
  `Message from the operator:`. Every retry of a model call is a `modelCallRetried` event, so a reader knows the text before it in the
  call is void; the gateway reports each retry, not only those after part of a reply.
- Verified against the API's documentation (compaction on demand, refusals and fallback, the skill's reference for the other
  fields), not against the live API: the recordings are written from the documented shapes. The owner's first live run should
  record one exchange of each feature (`LiveTests` shows how) before it is relied on. The compaction one should also confirm two
  points the documentation leaves open: the summary request carries the history up to the current turn, which ends with the last
  turn's reply and so is not exactly the messages of a request already sent, as the documentation describes; and on-demand
  compaction works on Opus 4.6 and Sonnet 4.6, which its compatibility list (not in the skill) should say.
- Follow-up, not in v1: server tools' per-use fees (such as web search's) are priced nowhere, for a fallback attempt or any other
  call; tokens are.
- Not built, as the decisions above say: strict tools (DESIGN.md §9 says why), Message Batches (CLD-11) and `baseUrl`.
- `sof config validate` does not yet run the provider checks that `sof run` does at start-up (provider tools, features, shortening);
  part 4 makes it.

## Part 4: the core follow-ups

- RUN-05: `capabilities.taskBoard.budget` is now `{ cost, tokens, toolCalls, time }` (it was the cost alone, `8`). A task keeps what its
  turns spent of each (`BoardTask.SpentTokens`, `SpentToolCalls`, `SpentTime`); a turn checks them with its own spending before each
  model call, warns at 80% of each, and a used-up limit ends the turn in a handoff naming it (`the task's token budget is used up`)
  and sends the task back to the lead. Every turn on a task is charged, not only one that cost money. A task's owner-set budget is
  still its cost; the other limits are the configuration's.
- RUN-05: a pattern's step agent with `budget.total` has its own level over all its steps in the run, beside the levels its turns draw
  on (`Budget.Alongside`): what it spends counts at both, either can be used up, and a resumed run starts it with what the agent had
  spent. A team's agents keep their own levels, as before, and an agent that is the run's entry agent keeps its one level.
- CFG-06: `sof config validate` reports what `sof run` refuses as it starts: an `extension:` tool, gate, check, knowledge source or
  pattern `sof` does not register (`WorkspaceHost.RegistrationErrors`), and what a model's provider lacks: a provider tool, a feature,
  turn-scoped messages for a fallback, a history shortening (`AgentRunner.ProviderErrors`, which the runner itself uses). It opens
  nothing and calls no model. Loading a configuration does not report the extensions, as a configuration may be meant for another
  host (the samples are).
- CFG-12: `sof config dry-run` runs a configuration with the workspace's tools over `InMemoryWorkspace`, and the sandbox's tools, its
  command rules and the command checks over a `FakeSandbox` whose every command answers that it was not run, exit code 0.
- ING-02: a proposal to project memory that holds a masking token (`[email-1]`, by the configured patterns' names, of any run) is
  refused. Restoring the value was the other way, but memory goes into every later model call's prefix, so the value would reach
  the model that masking keeps it from.
- RUN-06, LOOP-02: a turn left behind after `run.cancelWithin` keeps the agent's lock until it stops, so the agent's next turn waits
  for it, with no upper bound, and a warning event says so; the result counts what the turn had spent so far (`Turn.SoFar`).
- `capabilities.taskBoard.budget` written as a number (the old form) is a configuration error that says to write `{ "cost": N }`, as
  the binder would skip it and the default would apply. A failed task retried starts every one of its limits again.
- `sof config validate` also reports an agent's model whose provider this build of `sof` has not.
- Follow-up, not in v1: a cancelled turn, or one whose process died, is not charged to its task (as before for cost, now for its
  tokens, tool calls and time too), since the charge is made with the turn's cancelled token at its end.

## Notes

- HITL-07 (a MAY: end users rate a result, linked to its run) moved here from S16: build it if time allows, or record
  it as not in v1. Recorded as not in v1 above.
