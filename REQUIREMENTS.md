# Officina — Requirements

Status: draft 4 · 2026-10-05. Architecture: [`ARCHITECTURE.md`](ARCHITECTURE.md). Namespace: `Sleepyshark.Officina`.

## 1. Purpose

Officina is a .NET library for building agentic applications: agents that call models, use tools and follow control
flow. It is generic, but it grows **from real applications**: each part enters the core when an application needs it,
and takes its general shape when a second application needs it too.

This document has two horizons:

- **Phase 1** (§2, §4): the core, accepted against one reference application. Every requirement here is MUST.
- **North star** (§5): where the core is heading. Nothing there is built until its entry condition is met.

It replaces the requirements of `agentic-core` (the first Officina), whose proven parts are reused (see the
architecture, §13).

## 2. The reference application

Phase 1 is accepted against one application: **Bookshop Assistant**, a fully interactive console chatbot for the staff
of a small bookshop. It reads and changes a real PostgreSQL database, run in Docker, through tools. It is chosen to show
every phase 1 capability in one place.

| ID | Requirement |
|---|---|
| APP-01 | A console chat: the user types a message, the reply streams as it is generated, and tool activity is shown as it happens (which tool, what for, the outcome). |
| APP-02 | Commands: `/help`, `/new` (new session), `/sessions` (list, with titles), `/resume <id>`, `/memory` (show what is remembered), `/cost` (session usage and cost), `/audit` (recent audit entries), `/quit`. |
| APP-03 | Ctrl+C cancels the reply in progress, not the application; the session stays usable. |
| APP-04 | The database is PostgreSQL in Docker (one compose file), seeded with books, authors, customers, orders and stock. The application only reaches it through its tools. |
| APP-05 | Read tools: search books (by title, author, genre, price, in stock), get a book, find a customer, list a customer's orders, get an order. Read calls of one reply run in parallel. |
| APP-06 | Write tools: add a customer, place an order (checks and reserves stock in one transaction), cancel an order (returns stock), restock a book. Each needs the user's approval, shown with its exact input. |
| APP-07 | A business rule failure (not enough stock, unknown customer) comes back to the model as an error result, and the model recovers within the same reply: it explains, or tries another way. |
| APP-08 | There is no tool that runs arbitrary SQL; every query is fixed and parameterized. |
| APP-09 | Multi-step requests work as one agentic loop of several turns, for example *"Order the two cheapest fantasy books in stock for Alice Martin and tell me the total"*: find the customer, search, check, place the order after approval, answer. |
| APP-10 | Sessions (conversations) are stored in the database and survive a restart; `/resume` continues one with its cache intact. A session whose agent definition changed is refused, and a new session is offered. |
| APP-11 | Memory per staff member (chosen at start): preferences and notes the assistant keeps across sessions, for example *"I prefer prices with tax"*. |
| APP-12 | An MCP server (the reference filesystem server, in Docker) lets the assistant export reports, for example an order history as CSV, into an `exports` folder. Writing a file needs approval. |
| APP-13 | Run context gives the date and the staff member's name, appended per message, never in the instructions. |
| APP-14 | After each reply a status line shows tokens, cache read share, the reply's cost and the session's cost. A per-reply and a per-session budget apply; reaching one stops the reply and says why. |
| APP-15 | When a session is left, a separate **stateless** agent with **typed output** writes the session's title, a summary and the changes made, which `/sessions` shows. |
| APP-16 | Audit entries go to a database table through the application's own audit sink; `/audit` shows the latest. |
| APP-17 | A demo mode lowers the compaction threshold so compaction is visible in a short session, and reports it when it happens. |
| APP-18 | If the database stops mid-session, tools return errors, the assistant says so, and the session continues once it is back. |
| APP-19 | A demo script (`docs/demo.md`) walks through every capability above with the prompts to type and what to expect. |

## 3. Principles

1. **Applications first.** No feature, setting or abstraction without a named application that needs it now.
2. **Code first.** Agents are defined through the library's programming interface. Configuration files come later, if an application needs them (NS-10).
3. **One primitive.** An agent run (a turn loop) is the only primitive; every later pattern, team included, composes it.
4. **Decisions follow structured signals:** stop reasons, tool calls, schema-validated output, check results. The core
   never interprets free text to decide what happens next.
5. **The conversation is append-only.** Nothing already sent to the model is edited or removed by the core. Shortening
   is done by the provider's own context management. This keeps the prompt cache valid and keeps reasoning blocks valid
   (Claude binds them to the exact prefix that produced them).
6. **The prefix is stable.** Tools, instructions and the model are fixed for a conversation's life. Anything that varies
   (date, user, retrieved context, reminders) is appended after the cached prefix, never edited into it.
7. **Guarantees are enforced in code,** never requested in a prompt.
8. **Every run ends in a result**: completed, stopped (with a reason) or failed. Model behaviour never throws.
9. **Provider features are used, not abstracted away.** The neutral model interface covers what applications share;
   a provider's native features (server-side compaction, its memory tool, caching) are used where they exist.
10. **Tests replace only system boundaries:** model provider, network, tool back ends, MCP servers, clock, environment,
    storage back ends, the human.
11. **Small core.** Phase 1's core is about 3,500 lines or less; the Claude and MCP packages are separate. Growing past
    it needs a reason.

## 4. Phase 1 requirements

### Generality (GEN)

The core serves any agentic purpose, not only the reference application. These requirements keep it neutral.

| ID | Requirement |
|---|---|
| GEN-01 | The core holds no domain concept, user interface, storage technology or transport. Everything application-specific comes in through the contracts: model, tools, context, memory store, approver, audit sink, output type. |
| GEN-02 | Every part of an agent other than its model and instructions is optional: tools, conversation, memory, output type, approver, budget. An agent with only a model and instructions is valid. |
| GEN-03 | A run can be **stateless** (a new conversation discarded afterwards: extraction, classification) or **stateful** (a conversation the host keeps: chat, assistants). |
| GEN-04 | A run can be **interactive** (an approver answers) or **unattended** (no approver: a tool that needs approval is denied, and the model is told why). |
| GEN-05 | A run's result can be text, typed output, or only its side effects through tools. |
| GEN-06 | The phase 1 tests include offline, scripted samples of each purpose in the architecture's §8 table that phase 1 covers, beyond the reference application. |

### Agent and turn loop (AGT)

| ID | Requirement |
|---|---|
| AGT-01 | An agent is defined in code: model, instructions, tools, output format, budget, memory, approver. The definition is immutable and safe to share across threads. |
| AGT-02 | A run takes a conversation and a new user message, loops model call → tool calls → model call until the model stops, and returns a result. |
| AGT-03 | A run ends in exactly one result: `Completed` (text or typed output), `Stopped` (budget, refusal, output limit, context full, cancelled, iteration limit) or `Failed` (a provider error left after retries, or invalid output). |
| AGT-04 | Many runs of one agent can execute concurrently, each on its own conversation. |
| AGT-05 | The host can cancel a run at any time; it ends as `Stopped(Cancelled)`. A cancelled run leaves the conversation valid: no tool call without its result. |
| AGT-06 | The conversation is a plain object the host owns: it serializes to JSON and back, so the host keeps it in its own storage. |
| AGT-07 | A server tool's `pause_turn` stop continues the loop by sending the conversation back as is. |

### Models (MDL)

| ID | Requirement |
|---|---|
| MDL-01 | The core reaches models only through its own model interface: streamed text, content blocks, usage and stop reason. |
| MDL-02 | A Claude implementation ships, on the official Anthropic C# SDK, which is used in that package only. Requests are streamed. |
| MDL-03 | Claude settings (model, effort, thinking display, max output tokens, refusal fallback) are set on the Claude model object and fixed for a conversation. Effort is set explicitly, never left to a model default. |
| MDL-04 | Transient failures (rate limits, overload, network, including errors mid-stream) are retried with backoff, honouring `Retry-After`. What is left becomes `Failed`. |
| MDL-05 | Every content block the model returns (text, reasoning, tool use, server tool results, compaction) is kept in the conversation exactly as received, and replayed unchanged. |
| MDL-06 | A refusal is a `Stopped(Refusal)` result with the provider's category. The Claude model can opt into the server-side refusal fallback. |

### Context and caching (CTX)

| ID | Requirement |
|---|---|
| CTX-01 | A request is laid out as: tools (sorted by name, serialized deterministically) → instructions (frozen) → conversation. Nothing per-request or per-user appears in tools or instructions. |
| CTX-02 | Context that varies per run or per turn (date, user profile, retrieved passages, reminders) is appended to the conversation as an operator message after the cached prefix, through a run-context hook the host supplies. |
| CTX-03 | The Claude model places cache breakpoints: one on the last instructions block, with a TTL the agent chooses (5 minutes by default, 1 hour for conversations where users reply slowly), and automatic caching on the conversation's tail. |
| CTX-04 | Tools and model never change within a conversation. Changing either starts a new conversation. |
| CTX-05 | Usage reports cache reads and writes per call, and telemetry exposes the cache hit ratio per agent. |
| CTX-06 | All tool results of one model reply go back in a single message, in the order of the calls. |

### History compaction (HIST)

| ID | Requirement |
|---|---|
| HIST-01 | A conversation that nears the context window is compacted by the provider's server-side compaction (Claude: `compact` edit), at a token threshold the agent sets. The compaction block is kept in the conversation (MDL-05). |
| HIST-02 | Old tool results can be cleared by the provider's server-side context editing (Claude: `clear_tool_uses`), with a threshold and a number of recent results to keep. |
| HIST-03 | The core never edits or drops earlier messages itself (principle 5). A provider without server-side compaction reports it, and the run ends as `Stopped(ContextFull)` when the window is reached. |
| HIST-04 | Compaction and clearing are reported as events with the tokens they removed. |

### Memory (MEM)

| ID | Requirement |
|---|---|
| MEM-01 | An agent can have memory: a set of files the model views, creates, edits and deletes across conversations, through Claude's memory tool (`memory_20250818`) on Claude. |
| MEM-02 | Memory is stored through a store interface the host chooses; a file-system store and an in-memory store ship. |
| MEM-03 | Memory is scoped per run by a key the host gives (for example a user id): a run sees only its scope's files. Paths outside the scope are refused. |
| MEM-04 | Memory writes go through the tool pipeline like any write tool, so they are logged and can require approval. |
| MEM-05 | Memory is never put in the instructions; the model reads it on demand, so the cached prefix stays stable. |

### Tools (TOOL)

| ID | Requirement |
|---|---|
| TOOL-01 | A tool has a name, a description, a JSON input schema and an async handler. A tool can be built from an ordinary typed function of the application, with the schema derived from its parameters. |
| TOOL-02 | Tool input is validated against its schema before the handler runs (input streams eagerly, so the API does not validate it). Invalid input goes back to the model as an error result. |
| TOOL-03 | A tool declares whether it is a **read** or a **write** tool. Read tools of one reply run concurrently; write tools run one at a time, in order. |
| TOOL-04 | A tool can require approval: before it runs, the host's approver is asked, and a denial goes back to the model as the tool's result (APP-06). |
| TOOL-05 | A tool error (exception or error result) is returned to the model as an error result (`is_error`); it never ends the run. |
| TOOL-06 | A tool result over a size limit is truncated when it is created, and the model is told it was. |
| TOOL-07 | Provider server tools (for example web search) can be added to an agent; their calls and results are recorded and counted towards budgets. |

### MCP (MCP)

| ID | Requirement |
|---|---|
| MCP-01 | Tools can come from MCP servers over stdio and Streamable HTTP, through Officina's own MCP client. |
| MCP-02 | MCP tools go through the same tool pipeline as any tool: validation, approval, truncation, events. Each server's tools are marked read or write by the host (MCP annotations as a default). |
| MCP-03 | An MCP server's tool list is read once and pinned for the conversation (CTX-04), named `<server>__<tool>`, and filtered by an allow-list the host gives. |
| MCP-04 | A server that is down at the start of a run fails the run with a clear error; one that fails mid-run returns error results for its calls. |

### Output (OUT)

| ID | Requirement |
|---|---|
| OUT-01 | An agent can require typed output: an application type whose JSON schema is sent as the provider's structured output format, and the reply is deserialized into it. |
| OUT-02 | Output that fails validation is sent back once for correction; failing again ends the run as `Failed` with the validation errors. |

### Budgets (BUD)

| ID | Requirement |
|---|---|
| BUD-01 | A run can be limited by cost (currency), tokens, model calls and wall time. Each limit is checked before every model call. |
| BUD-02 | Cost is computed from a price table per model that prices cache reads and writes, and the host can replace it. |
| BUD-03 | Every result reports usage: tokens (input, output, cache read, cache write), cost, model calls, tool calls and duration. |

### Events and observability (EVT)

| ID | Requirement |
|---|---|
| EVT-01 | A run can be consumed as a stream of events: text deltas, reasoning updates, tool call started and finished, approval asked and answered, compaction, usage, result. |
| EVT-02 | Runs, model calls and tool calls emit OpenTelemetry traces and metrics. |
| EVT-03 | Secrets passed to the core (API keys, MCP credentials) never appear in events, traces or exceptions. |

### Audit (AUD)

Events (EVT) are for watching a run live; the audit trail is the durable record of what an agent did, for review after
the fact.

| ID | Requirement |
|---|---|
| AUD-01 | Important events are written to an audit trail: run started and ended (result, usage, cost); every tool call (tool, input, outcome, duration); approvals asked and answered; memory writes; budget stops; refusals; compactions; provider failures; prefix mismatches; MCP servers connected, failed or disconnected. |
| AUD-02 | A write tool's attempt is recorded **before** it runs and its outcome after. If the attempt cannot be recorded, the tool does not run and the model gets an error result. |
| AUD-03 | Each entry carries the time, a sequence number, the run, the conversation, the agent and the memory scope, so a run can be reconstructed. Entries are never changed or removed by the core. |
| AUD-04 | The audit trail is written through an audit sink contract the host chooses; a JSON-lines file sink ships. |
| AUD-05 | Secrets never reach the audit trail, and large inputs and results are truncated with their size noted. |

### Testing (TEST)

| ID | Requirement |
|---|---|
| TEST-01 | A test kit ships with a scripted model (replies given in advance, requests recorded), a scripted approver, an in-memory memory store and a fake MCP server, so any agent runs offline and deterministically. |
| TEST-02 | A **prefix stability** check: across the calls of a scripted multi-turn run, each request's tools, instructions and earlier messages are byte-identical to the previous request's. It runs for the reference application and every GEN-06 sample. |
| TEST-03 | The test suite needs no API key and no network; CI runs it on Linux and Windows. |
| TEST-04 | The reference application has a live smoke test against the Docker database, run on demand with an API key, which drives APP-09, asserts cache reads from the second call on, and forces a compaction. |
| TEST-05 | A dependency check, run as a test, fails if the Anthropic SDK is referenced outside the Claude package. |

### Phase 1 acceptance

- Bookshop Assistant is built on the core in this repository, its live smoke test passes, and the demo script runs as written.
- Every requirement above has a test.
- The core holds no code that neither the application nor a GEN-06 sample uses.

## 5. North star

Each item enters a phase only when its **entry condition** is met. Until then it is not designed in detail and not built;
the architecture only keeps room for it.

| ID | Capability | What it means | Enters when |
|---|---|---|---|
| NS-01 | **Workflows** | Compose agent runs: sequence, routing, parallel fan-out, evaluate-and-retry, nested | An application needs two or more agents in a fixed flow |
| NS-02 | **Multi-agent teams** | A lead plans tasks, members work them, checks decide done, a task board holds state | An application needs agents that plan and divide work |
| NS-03 | **Durable runs** | Checkpoint a run, resume after a crash, roll back; long runs over hours or days | An application runs longer than a process is trusted to live |
| NS-04 | ~~History shortening~~ | Moved to phase 1 (HIST) | — |
| NS-05 | ~~Memory~~ | Moved to phase 1 (MEM) | — |
| NS-06 | ~~MCP~~ | Moved to phase 1 (MCP) | — |
| NS-07 | **Sandbox and workspace** | Run commands in an OS sandbox; work in git working copies | An application runs model-written code or commands |
| NS-08 | **More providers** | A second model provider behind the neutral interface; fallback between models | An application needs a model Claude does not serve |
| NS-09 | **Policies and gates** | Declarative permission rules and code gates on tool calls, beyond per-tool approval | An application needs rules that depend on arguments or caller |
| NS-10 | **Configuration files** | Agents described in JSON with a schema | An application's agents are edited by non-developers or per tenant |
| NS-11 | **Knowledge sources** | Retrieval as a first-class context source, not only as a tool | Two applications repeat the same retrieval plumbing |
| NS-12 | **Evaluation** | Datasets, graders and regression runs against the live model | An application needs a quality bar checked over time |
| NS-13 | **Hosting helpers** | ASP.NET Core integration: DI registration, streaming endpoints | Two applications repeat the same hosting code |
| NS-14 | **Batch** | Many independent runs through the provider's batch API | An application processes work in bulk where latency does not matter |
| NS-15 | **Large tool sets** | Tool search (deferred tool loading) and mid-conversation tool changes, so tools can grow without breaking the cache | An agent has more tools than fit usefully in every request |
| NS-16 | **Programmatic tool calling** | The model calls tools from code execution, so large intermediate results never enter the context | An agent chains many tool calls over large data |
| NS-17 | **Subagents** | A run delegates a sub-task to another agent, possibly on a cheaper model, without switching its own model (CTX-04) | An application needs a cheaper model or a clean context for a sub-task |

## 6. Non-goals

- A coding agent or coding team competing with Claude Code. Use Claude Code to write software.
- A graphical designer, a hosted service or a workflow engine without models.
- Client-side history editing or summarizing (principle 5).
- Adapting to another agent framework (Microsoft Agent Framework, Semantic Kernel, LangChain).

## 7. Decisions and open questions

| # | Decision or question | Status |
|---|---|---|
| D1 | .NET 10, Linux and Windows. | Decided |
| D2 | Own model interface, not `Microsoft.Extensions.AI`. **Reason:** phase 1 has one provider, the interface is small, and principle 9 needs Claude features used directly. Revisit at NS-08. | Decided for phase 1 |
| D3 | The core stores no conversations; the host persists them (AGT-06). Memory has a store interface (MEM-02). | Decided |
| D4 | History compaction, memory and MCP are in phase 1. | Decided (owner) |
| D5 | Compaction is server-side only (HIST-03). **Reason:** client-side edits break the prompt cache and invalidate Claude's reasoning blocks, which new accounts enforce with a 400. | Decided |
| D6 | MCP through Officina's own client, not Claude's MCP connector. **Reason:** the connector runs tools on Anthropic's side, outside the tool pipeline (no approval, no stdio servers). The connector may be added later for remote servers that need none of that. | Decided |
| D7 | One reference application for phase 1: Bookshop Assistant, a console chatbot over PostgreSQL in Docker (§2). | Decided (owner) |
| D8 | Namespace and package prefix: `Sleepyshark.Officina`. It takes over from `agentic-core`, which is archived. | Decided (owner) |
| D9 | Audit is separate from events: events stream to the host for display; audit is a durable trail through its own sink (AUD). | Decided |
| Q1 | Which PostgreSQL client library: a plain data provider or an object mapper? Recommended: the plain provider, as the queries are few and fixed. | Proposed |
