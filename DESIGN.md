# Officina — Design

Status: draft for M0 design review · 2026-09-30 · implements `REQUIREMENTS.md` (revision 2),
configured by `CONFIGURATION.md`. The diagrams are in `docs/diagrams/`: an SVG for embedding, and an HTML page for
viewing. They are generated with the diagram-design skill (default style).

## 1. Solution layout

![Solution layout](docs/diagrams/solution-layout.svg)

<sub>[Open the HTML version](docs/diagrams/solution-layout.html)</sub>

Officina is the product, and Sleepyshark is the organization: all projects live under the
`Sleepyshark.Officina.*` namespace. The CLI command is `sof`. Each project is one assembly, and its root
namespace is its name.

| Project | Box in the diagram | What it contains | Depends on | Loaded when |
|---|---|---|---|---|
| `Sleepyshark.Officina.Core` | Core | Options classes and validation; turn engine; built-in loop patterns; context builder; tool pipeline; run record; budgets; events; audit; extension interfaces (§4); built-in `record.*`, `control.*` and `artifact.*` tools | .NET base library and a JSON Schema validator only | Always |
| `Sleepyshark.Officina.Hosting` | not drawn | Binds `sof.json`, environment variables and CLI flags onto the Options classes. Registers services and loads extension assemblies. | Core, `Microsoft.Extensions.Configuration` | By any host application |
| `Sleepyshark.Officina.Team` | Capabilities | Team pattern, lead role support, task board, helper agents; `tasks.*` and `team.*` tools | Core | `team` or `taskBoard` is on |
| `Sleepyshark.Officina.Workspace` | Capabilities | Git-backed workspace: baseline, working copies, integration queue, edit safety; `workspace.*` tools | Core; the git CLI at run time | `workspace` is on |
| `Sleepyshark.Officina.Sandbox` | Capabilities | Linux sandbox (bubblewrap, cgroups v2) and Windows sandbox (AppContainer, Job Objects); filtering network proxy; `sandbox.*` tools | Core | `sandbox` is on |
| `Sleepyshark.Officina.Capabilities` | Capabilities | Conversation store, human interaction, project memory, checkpoints, knowledge retrieval; `human.*`, `memory.*` and `knowledge.*` tools | Core | Each part when its capability is on |
| `Sleepyshark.Officina.Storage.Sqlite` | Storage.Sqlite | Default storage: one SQLite file in WAL mode, plus artifact files on disk | Core, `Microsoft.Data.Sqlite` | Configured as storage (the CLI's default) |
| `Sleepyshark.Officina.Mcp` | Mcp | Own MCP client for stdio and Streamable HTTP. Turns each server tool into a core tool. | Core | `toolServers` are configured |
| `Sleepyshark.Officina.Providers.Claude` | Providers.Claude | The Claude provider: maps requests (§9), places cache markers, streams, classifies errors, ships the price table | Core, Anthropic C# SDK | A `claude` provider is configured (the default) |
| `Sleepyshark.Officina.Testing` | Testing | Test kit: scripted models, controllable clock, fake tools, in-memory workspace, sandbox and storage, record and replay (TEST-01, TEST-02) | Core | In tests only |
| `Sleepyshark.Officina.Cli` | Cli | The coding team CLI: `init`, `run`, `resume`, `config`; approvals, questions, task board and cost views; the CLI human-interaction channel | Every project except Testing; it receives the Anthropic SDK only transitively, through the Claude provider | — |
| `Anthropic` (NuGet) | Anthropic SDK | The official Claude SDK | `Microsoft.Extensions.AI.Abstractions` (transitive) | With the Claude provider only |

Dependency rules, enforced by the dependency check, which runs as a test in CI (TEST-32):

- Every project depends on `Sleepyshark.Officina.Core`. Core depends on no other project and on no AI or
  agent framework.
- Only `Sleepyshark.Officina.Providers.Claude` references the Anthropic SDK directly. Projects that
  reference the provider, such as the CLI, receive the SDK and its transitive
  `Microsoft.Extensions.AI.Abstractions` through it, but no project code outside the provider uses
  either.
- Capability projects never reference each other. Where one needs another, for example the sandbox
  needing the workspace, it uses that capability's interface in Core, and validation checks that
  both are on (CAP-03).
- A capability that is off is not loaded, so it adds no tools, storage or settings (CAP-02).

## 2. Runtime

![Runtime](docs/diagrams/runtime.svg)

<sub>[Open the HTML version](docs/diagrams/runtime.html)</sub>

- **Agent actor.** Each agent has a mailbox (`Channel<T>`) and processes one turn at a time
  (LOOP-02). Messages that arrive during a turn are appended at the next iteration (CTX-08).
- **Turn engine.** This is the only primitive (principle 4). Patterns, including the team, call
  `RunTurnAsync(slot, input)` and nothing else, so budgets, cancellation, events and handoffs
  behave the same everywhere.
- **Run coordinator.** It serializes all shared-state writes: record proposals, task changes and
  memory changes. Each write is one SQLite transaction with a revision check (REC-02, REC-04,
  CONC-01).
- **Model gateway.** Per provider, it runs a token bucket fed by the provider's rate-limit
  responses. Agents are served round-robin, with the lead first (MDL-08).
- **Event bus.** Events are appended to the stored log with a sequence number per agent. Each live
  consumer has a bounded queue. A consumer that falls behind is detached and catches up from the
  log, so it never blocks an agent (EVT-03, EVT-04).

## 3. Model request and caching

![Model request and caching](docs/diagrams/model-request.svg)

<sub>[Open the HTML version](docs/diagrams/model-request.html)</sub>

Every model call is built from five parts, always in this order (CTX-01). Parts 1–3 are the stable
prefix. They are built once, when a conversation starts, and never change for that conversation.

| # | Part | What it contains | Built from | Changes when | Sent to Claude as |
|---|---|---|---|---|---|
| 1 | Tool definitions | Name, description and input schema of each tool the model slot offers, sorted by name | The slot's tool sets and the enabled capabilities (TOOL-03); MCP tool lists read at startup | Only for new conversations, after a configuration change or a tool server's list changes | `tools` |
| 2 | Instructions and policies | The agent's instructions, with placeholders filled from project and agent values only; output format rules; how content from tools, documents and other agents is labelled as data (INV-08) | The agent definition | Only for new conversations, after a configuration change | First `system` block, then boundary ① |
| 3 | Project memory | Conventions, how to build and test, architecture summary, project-wide decisions (MEM-01, MEM-02) | The memory store, read when the conversation starts | Only for new conversations. Running conversations receive approved changes as appended operator messages instead (MEM-03). | Second `system` block, then boundary ② |
| 4 | History | Everything said so far: each turn's work, labelled with its sender; the model's replies, including reasoning blocks exactly as received (MSG-02); tool calls and their trimmed results; messages received during a turn (CTX-08); operator messages; shortening summaries | The conversation store | Grows by appending only. Earlier content is changed only by history shortening (HIST). | `messages`, then boundary ③ on the last cacheable block |
| 5 | Volatile context | Facts with source and as-of time, in a stable order (CTX-07); passages retrieved before the turn, with citation ids; findings and decisions; the current task's status and acceptance criteria; operating facts such as the caller and the date (CTX-09) | Run record, task board, knowledge sources and host, rebuilt for every call (LOOP-10) | Every call | A turn-scoped mid-conversation `system` message; where unsupported, a text block after the tool results (CTX-10) |

The three cache boundaries (CTX-11):

| Boundary | Covers | Shared by | Lifetime | Why there |
|---|---|---|---|---|
| ① | Parts 1–2 | Every agent with the same definition and model slot, in every project | 1 hour | Agents of one role start throughout a run, often minutes apart |
| ② | Parts 1–3 | Every agent with the same definition, model slot and memory scope | 1 hour | Memory differs per project, so it gets its own boundary after the shared part |
| ③ | Parts 1–4 | One conversation | 5 minutes, configurable | Calls within a turn come seconds apart. Agents that often wait for approvals can use 1 hour. |

Claude allows at most four boundaries and requires longer lifetimes before shorter ones; this
layout uses three. A prefix shorter than the model's minimum cacheable size simply isn't cached.
Cache reads and writes are measured on every call, and a low hit rate raises a warning (COST-01).

How each call is assembled:

1. Take the conversation's stable prefix (parts 1–3) unchanged.
2. Take its history (part 4) and append what is new since the last call: the model's last reply,
   the results of the tools it asked for, and any messages that arrived.
3. Build the volatile context (part 5) and add it at the end. In the fallback form, after the first
   call of a turn only what changed since it was last sent is appended.
4. Place boundary ③ on the last cacheable block of history.
5. Check that the request starts with exactly the content of the previous request (the append-only
   check). It runs in debug builds and in TEST-09.
6. Send the request through the model gateway.

## 4. Extension interfaces

An application adds behaviour only through these interfaces (§4.3 of the requirements). They are
in `Sleepyshark.Officina.Core.Extensibility`.

```csharp
public interface IModelProvider    { ProviderCapabilities Capabilities { get; }
                                     IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct); }
public interface ITool             { ToolDescriptor Descriptor { get; }
                                     ValueTask<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct); }
public interface IGate             { ValueTask<GateDecision> EvaluateAsync(GateContext context, CancellationToken ct); }
public interface ICheck            { ValueTask<CheckResult> RunAsync(CheckContext context, CancellationToken ct); }
public interface IKnowledgeSource  { ValueTask<Retrieval> RetrieveAsync(RetrievalQuery query, CancellationToken ct); }
public interface ILoopPattern      { ValueTask<StepOutcome> RunAsync(PatternContext context, CancellationToken ct); }
public interface IHumanChannel     { ValueTask<HumanAnswer> AskAsync(HumanRequest request, CancellationToken ct); }
public interface IEventConsumer    { ValueTask OnEventAsync(CoreEvent e, CancellationToken ct); }
public interface IStorage          { IRunStore Runs { get; }  IConversationStore Conversations { get; }  IRecordStore Records { get; }
                                     ITaskStore Tasks { get; }  IMemoryStore Memory { get; }  ICheckpointStore Checkpoints { get; }
                                     IEventLog Events { get; }  IAuditLog Audit { get; }  IArtifactStore Artifacts { get; } }
public interface IMasker           { MaskResult Mask(string text, MaskScope scope);
                                     string Restore(string text, MaskScope scope); }
public interface IHistoryShortener { ValueTask<History> ShortenAsync(History history, ShortenRequest request, CancellationToken ct); }
public interface IWorkspace        { ValueTask<IWorkingCopy> OpenWorkingCopyAsync(TaskId task, AgentId agent, CancellationToken ct);
                                     ValueTask<IntegrationResult> IntegrateAsync(IWorkingCopy copy, CancellationToken ct);
                                     ValueTask<WorkspaceSnapshot> SnapshotAsync(CancellationToken ct);
                                     ValueTask RestoreAsync(WorkspaceSnapshot snapshot, CancellationToken ct); }
public interface ISandbox          { SandboxSupport Probe();
                                     ValueTask<ISandboxProcess> StartAsync(SandboxCommand command, CancellationToken ct); }
public interface ISecretSource     { ValueTask<SecretValue> GetAsync(string name, CancellationToken ct); }
```

| Interface | Implement it to | Called by the core when | Receives | Returns | Rules the core enforces |
|---|---|---|---|---|---|
| `IModelProvider` | Add a model provider | The model gateway sends a model call | `ModelRequest`: prefix, history, volatile context, cache boundaries, profile settings | A stream of `ModelEvent`: text, reasoning, tool requests, usage, stop reason | `Capabilities` are checked at validation (MDL-06). Failures must be classified (MDL-05). It receives no secrets except its own credential. |
| `ITool` | Add an application tool | The model asks for the tool and the tool pipeline lets the call through (§5) | `ToolCall`: validated arguments, caller identity, idempotency key, read-only view of the run record | `ToolResult`: content, artifacts, or an error category (TOOL-08) | `Descriptor` declares the name, input schema, kind (read or write) and defaults. A write tool needs a gate (INV-04). No access to configuration, budgets or other agents (INV-10). |
| `IGate` | Add a rule that needs code | Before each call of a tool it is attached to (TOOL-05) | `GateContext`: tool, arguments, caller, run record, task board when on, and whether the agent has read untrusted content | `GateDecision`: allow, deny with a reason, ask a human, or route | Deterministic: no model calls and no side effects. |
| `ICheck` | Add an output or verification check | Output is produced (OUT-03), a task is submitted (TASK-05), or a change is integrated (WS-02) | `CheckContext`: output, artifacts, read-only working copy, task | `CheckResult`: passed or failed, with findings | Only the result decides; nothing the agent says can override it (INV-09). Commands run in the sandbox. |
| `IKnowledgeSource` | Search the application's own data | Before a turn, or when the agent calls `knowledge.search` (CTX-04) | `RetrievalQuery`: question, caller, maximum passages | `Retrieval`: passages, citations, and covered, partly covered or not covered (CTX-05) | Results are labelled as data (INV-08). It sees only the caller's tenant (SEC-02). |
| `ILoopPattern` | Add a loop pattern | A step selects the pattern by name (PAT-07) | `PatternContext`: `RunTurnAsync`, nested-step runner, step outcomes, budget drawn from the parent | `StepOutcome`: completed, handed off, failed or cancelled | It can only call the core's primitives, so budgets, cancellation, events and handoffs apply (PAT-06). |
| `IHumanChannel` | Change how humans are reached | An approval, question, sign-off or owner message is needed (HITL) | `HumanRequest`: kind, agent, summary, pending action, deadline | `HumanAnswer`: approve, approve a changed version, deny, or text | The core applies timeouts (HITL-02). A changed version goes through the checks again. |
| `IEventConsumer` | Display or forward events | For each event, from its own bounded queue | `CoreEvent`: run, agent, step, sequence number, payload | Nothing | A slow or failing consumer is detached and catches up from the log; it never slows agents (EVT-04). |
| `IStorage` | Store state elsewhere | Whenever state is read or written | One store per kind of data: runs, conversations, records, tasks, memory, checkpoints, events, audit, artifacts | — | Conversations and the audit log are append-only. Every row carries its tenant (SEC-02) and format version (REL-04). Writes for one run are serialized. |
| `IMasker` | Replace masking | Work is admitted, or a configured tool result or passage arrives (ING-02) | Text and the run's masking scope | Masked text, and later the restored text for a tool allowed to see it (ING-06) | Restored values never reach the model, history or logs. |
| `IHistoryShortener` | Replace history shortening | History is too long, or the provider reports the input is too long (HIST-04) | The history and a target size | Shortened history | The result is checked (HIST-02). It cannot change the run record, tasks or memory (HIST-03). |
| `IWorkspace` | Replace the git workspace | File tools run, a task starts or ends, a change is integrated, or a checkpoint is taken | Task, agent, working copy, snapshot | `IWorkingCopy` (read, read range, search, edit with the expected content hash, write, delete, move), integration result, snapshot | Edits fail if the file changed since it was read (WS-07). Integration is queued (WS-09). Protected paths are enforced (WS-05). |
| `ISandbox` | Replace OS isolation | A command tool or a check runs a command | `SandboxCommand`: command, working copy, limits, network allow list, permitted secrets | `ISandboxProcess`: streamed output, exit code, stop | `Probe` must report whether isolation is available, and the core refuses to run commands without it (SBX-07). |
| `ISecretSource` | Read secrets from elsewhere | A secret is needed, at the moment of use | The secret's name | The value | Values are never logged or placed in model input (INV-06). |

## 5. Tool call path (TOOL-05, TOOL-07)

Every tool call goes through these steps in order. The first step that does not allow the call
decides the outcome.

| Step | Possible results | If the call doesn't continue |
|---|---|---|
| Validate arguments | pass, or invalid | Error sent to the model ("invalid arguments") |
| Permission rules | allow, deny, ask, route | Deny → "not authorised"; ask → human; route → another agent |
| Gates for all tools, then the tool's own gates | allow, deny, ask, route | Same as above |
| Human approval (if required) | approve, approve a changed version, deny, time out | A changed version goes back to "validate arguments"; deny or time out → "approval denied" |
| Idempotency lookup (irreversible tools only) | new, or already recorded | Never re-run; goes to a human (TOOL-10, RUN-07) |
| Audit "intent" (durable) | — | — |
| Run as the caller | done, failed, timed out | Retried per configuration, then the error goes to the model (TOOL-08) |
| Trim result, audit "outcome", publish events | — | — |

- **Every write-tool attempt is audited**, whichever step it stops at: allowed, denied, asked,
  failed or completed (TOOL-11).
- **If every call in an iteration is denied**, the turn ends in a handoff for a policy gap (LOOP-11).
- **Provider server-side tools** skip the per-call steps. They are audited when they appear in the
  response (TOOL-13).

## 6. Team

![Team run](docs/diagrams/team-run.svg)

<sub>[Open the HTML version](docs/diagrams/team-run.html)</sub>

![Task states](docs/diagrams/task-states.svg)

<sub>[Open the HTML version](docs/diagrams/task-states.html)</sub>

## 7. Workspace and sandbox

| Concern | Design |
|---|---|
| Baseline and working copies | The baseline is a git branch. Each working copy is a `git worktree` on `agent/<task>`. The core uses the git CLI, because libgit2's worktree support is limited. |
| Integration (WS-09) | A single queue per workspace. Each change is rebased onto the current baseline in a scratch worktree, then the baseline checks run, then the baseline fast-forwards. A failure or conflict goes back to the task. |
| Edit safety (WS-07) | The core keeps a content hash for each agent and file at read time. An edit whose hash no longer matches fails. |
| Linux sandbox | bubblewrap (user namespaces). Only the working copy and toolchains are mounted, and the network namespace is empty. Allowed traffic goes through a host-side filtering proxy reached over a Unix socket in `$XDG_RUNTIME_DIR` (socket paths are limited to 108 bytes), bind-mounted into the sandbox and forwarded by socat. Limits use cgroups v2 through `systemd-run --user`, and cancel uses `cgroup.kill`. |
| Linux host setup | Done once by the installer, as root: an AppArmor profile that lets bubblewrap create user namespaces (Ubuntu 23.10 and later restrict them); `loginctl enable-linger` for service accounts, so they get a systemd user manager; socat installed. Everything else runs unprivileged (S00a). |
| Windows sandbox | An AppContainer with ACLs granting the working copy only, and a Job Object for CPU, memory, process count and kill-on-close. The network capability is removed. Allowed traffic goes through the filtering proxy over a named pipe whose access list admits only the container, with a small forwarder inside the sandbox. No admin rights are needed. A loopback exemption also works, but it would expose every local service on the host, so it is not used (S00a). |
| No isolation available | Startup refuses to run with a clear reason. Commands never run unsandboxed (SBX-07). |

## 8. State and durability

- **Storage.** SQLite in WAL mode, with a single writer per run, plus artifact files. Tables:
  runs (with the resolved configuration, CFG-07), conversations and messages (append-only), record
  entries, tasks and task history, memory, checkpoints, events, audit.
- **Checkpoints are cheap because history is append-only.** A checkpoint stores:
  - the message count of each conversation;
  - the revisions of the record, the task board and memory;
  - the commit SHA of each working copy (uncommitted changes are committed as WIP first).
- **Rollback (RUN-08)** truncates to those positions and resets the worktrees. The restored
  history is byte-identical to what was sent earlier, so it is still valid for the provider.

![Crash and resume](docs/diagrams/resume.svg)

<sub>[Open the HTML version](docs/diagrams/resume.html)</sub>

## 9. Claude provider mapping

| Core | Claude API |
|---|---|
| Stable prefix and cache boundaries ①② | `tools` (sorted), then `system` blocks with `cache_control`. `ttl: "1h"` before 5-minute boundaries. |
| History and cache boundary ③ | `messages`, with `cache_control` on the last cacheable block |
| Volatile context | Turn-scoped mid-conversation `system` message where supported; otherwise a text block after the `tool_result` blocks |
| Operator messages, memory changes | Mid-conversation `system` messages |
| Model profile | `model`, `output_config.effort`, `max_tokens`, `thinking` (adaptive; `display`), `tool_choice: auto`. Forced tool choice is not used, because current models reject it. |
| Structured output | `output_config.format`; `strict: true` on tools |
| Stop reasons | `end_turn` → finished, `tool_use` → wants tools, `max_tokens` → output limit, `stop_sequence`, `pause_turn` → paused, `refusal` → refused, `model_context_window_exceeded` → input too long, anything else → unknown |
| Errors (MDL-05) | Classified by the SDK's exception type, then its error type. 429 → rate-limited; 500, 529 and connection errors → transient; an error that arrives mid-stream (no status code, for example `overloaded_error`) → transient; 401 and 403 → authentication; a 400 whose message says the prompt is too long → input too long (the SDK has no distinct type, so the provider matches the API's error message, not model output); other 400s → invalid request |
| SDK use (S00b) | The provider uses only the beta API (`client.Beta.Messages`), because turn-scoped system messages, fallbacks, task budgets and context management are typed only there. JSON the core already holds (tool and output schemas, stored reasoning blocks) is passed through the SDK's raw-data constructors. The SDK's own retries are off (`MaxRetries = 0`), so the model gateway is the only retry policy, including for mid-stream errors (REL-01, CLD-10). |
| Usage | `input_tokens`, `output_tokens`, `cache_read_input_tokens`, `cache_creation_input_tokens` (by TTL) |
| Streaming | Always on. Tool inputs use eager streaming and are validated against the schema before any tool runs. |

## 10. Risks to settle early

| Risk | Plan |
|---|---|
| Sandbox gaps the spike did not cover: isolation between two sandboxes (SBX-06), output size caps, background processes, and Windows files readable by all apps (Program Files, Windows) | Prove them in S15; fallbacks for hosts that cannot isolate are rootless Podman (Linux) and Windows Sandbox or WSL2 (Windows) |
| Claude beta features (turn-scoped system messages, compaction) change or are not on the chosen model | Each one is behind a provider feature flag, with the append-only fallback always available |
| Integration queue throughput with slow test suites | Measure in M6. The option is to batch compatible changes into one baseline check run. |
| The 90% benchmark target depends on the model | The goal set's difficulty is agreed before M6 (TEST-31) |
