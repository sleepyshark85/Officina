# Officina — Requirements Specification

Status: revision 2 · 2026-09-30. v1 is built against it; [`docs/plan/README.md`](docs/plan/README.md) says what is left.

## 1. Purpose and scope

Officina is a universal engine for building AI agents. Any agent built from model calls,
tools and control flow can be described by **configuration**: which models it uses, which tools it
has, which loop pattern it follows, what it remembers, and how humans take part. Code is needed
only for genuinely new behaviour, such as an application's own tools or checks. It is never
needed to change the engine.

The core must serve, without being changed:

- a single-call assistant or extractor (one model call, structured output);
- a document Q&A assistant serving many users at once;
- a tool-using agent that acts on a user's behalf;
- fixed workflows, routers, parallel fan-out and generate-then-evaluate loops;
- a long-running, multi-agent development team driven from a command-line tool.

The **coding team is the primary target of v1**: every requirement the coding team needs is part
of v1, and it is the application v1 is accepted against. The other kinds of agent must stay
expressible by configuration alone; in v1 this is shown by the runnable pattern configurations
(TEST-06), and the document Q&A reference application follows after v1.

**In scope**

- the core engine and its configuration model;
- built-in loop patterns and optional capabilities (teams, tasks, workspace, sandbox, human
  approvals, project memory, checkpoints);
- integration with the Claude API as the first model provider;
- a test kit that runs agents and teams without calling a real model;
- one reference application, the **coding team CLI**, which takes an application from a goal to
  verified, working code; and one runnable configuration per built-in loop pattern.

**After v1:** the **document Q&A assistant** reference application, which uses no optional team
capabilities.

**Out of scope:** graphical user interfaces, hosting infrastructure, document indexing and search
infrastructure, general-purpose workflow automation without models, real-time voice, and any
business domain logic.

**Constraints**

- Platform: .NET 10, on Linux and Windows.
- The core is a .NET library, hosted in-process by the application (the coding team CLI is such a
  host). It needs no separate server.
- First model provider: the Claude API, implemented on the official Anthropic C# SDK (CLD-12).
- No third-party agent or AI abstraction framework, such as Microsoft Agent Framework or
  `Microsoft.Extensions.AI`, is used, and none is adapted to. The Anthropic C# SDK is the only
  model-related dependency, and only inside the Claude provider. The SDK itself depends on
  `Microsoft.Extensions.AI.Abstractions`. That package is allowed only as the SDK's own transitive
  dependency, only in the provider assembly, and no project code references its types. For the
  same reason, the core implements its own MCP client: the official MCP C# SDK depends on
  `Microsoft.Extensions.AI.Abstractions`.
- Configuration files are JSON, validated against a published JSON Schema (CFG-15).
- Storage is configurable; the core ships a default local storage (STO-01).
- v1 runs a team on a single machine.

**Reading the requirements**

- IDs are `AREA-NN`, for example `LOOP-03`. They are stable, so they can be cited later.
- **MUST** is required for v1 acceptance. **SHOULD** is expected in v1 unless it is deferred with a
  stated reason. **MAY** is optional.
- **Configurable** means the setting belongs to the configuration model (section 5). The stated
  default applies when it isn't set.

---

## 2. Glossary

| Term | Meaning |
|---|---|
| Application | A product built on the core. It supplies configuration and, only where needed, its own extensions. |
| Configuration | The complete, validated description of the agents an application runs: models, tools, loop patterns, context, capabilities, policies and limits. |
| Preset | A named, ready-made configuration for a common kind of agent, which an application can use as is or adjust. |
| Agent definition | The configuration of one kind of agent: its model profiles, instructions, tool set, loop pattern, context strategy, output format, budgets, policies and enabled capabilities. In a team, an agent definition is called a **role**. |
| Agent | A running instance of an agent definition. Each agent has its own conversation. |
| Model profile | A named choice of provider, model and model settings, with optional fallbacks. |
| Tool set | A named group of tools with their settings, assignable to agent definitions. |
| Loop pattern | The control flow an agent follows, such as a single call, a tool loop, a workflow or a team. Patterns can be nested. |
| Step | One part of a loop pattern: a model call, a tool loop, a check, a nested pattern, or a human input. |
| Capability | An optional part of the core that an application turns on, such as teams or a sandbox. A capability that is off costs nothing. |
| Extension | Application code that plugs into a defined extension point, for behaviour configuration cannot express. |
| Invariant | A guarantee the core always enforces. No configuration or extension can switch it off. |
| Trigger | How work reaches an agent: an interactive message, a single request, a batch, a schedule, an external event, or a long-running run. |
| Owner | The person or system a run acts for: an end user, a developer, or a service. All agents in the run act within the owner's permissions. |
| Caller | Whoever a request runs on behalf of: the owner, or an agent acting for the owner. Has an identity, a tenant and permissions. |
| Run | One unit of work towards an outcome. It can be as short as one request or as long as building an application over days. |
| Conversation | The sequence of messages one agent has with the model. |
| Turn | One unit of work given to an agent and everything it does to finish it. |
| Iteration | One model call within a turn, plus any tool calls it asks for. |
| Structured signal | A value the system can check without interpreting prose: a stop reason, output validated against a schema, a tool call, or a check result. |
| Stop reason | The model provider's structured statement of why the model stopped (finished, wants a tool, hit the output limit, refused, …). |
| Tool | A capability the model may ask to use, such as reading a file, searching documents or calling an external service. |
| Read tool / Write tool | A tool that only observes / a tool that changes something: data, files, processes or external systems. |
| Gate | A deterministic check that decides whether a specific tool call may run. |
| Check | A deterministic or reviewed test of an output or a piece of work: schema validation, citations resolving, build, tests, or review by another agent. |
| Run record | Durable state that only the core writes: facts, findings, decisions and citations. Agents propose changes through tools; the core validates each proposal and applies it. The model never writes to it directly. |
| Stable prefix | The part of the model's input that does not change between turns: tool descriptions, instructions, policies and project memory. It can be cached by the provider. |
| Volatile context | The part of the model's input that is rebuilt for every model call: facts, retrieved knowledge, findings and decisions, and the current task's status. It is delivered without editing history (CTX-10). |
| Model slot | A place in an agent definition or step where a model is used, with its model profile and its own tool sets. |
| Handoff | A structured package passing work to a human or to another agent. |
| Budget | Limits on tokens, cost, time and tool calls, at the run, agent, task and turn level. |
| Team, lead, task, task board, workspace, baseline, working copy, sandbox, checkpoint, project memory | Terms of the optional capabilities, defined in section 8. |

---

## 3. Design principles

These principles decide any trade-off in the requirements below.

1. **Configuration over code, invariants over configuration.** Anything known to vary between agents
   is configurable. A value becomes a setting only when there is a known case that needs a different
   value; until then it is a constant in code. The safety guarantees in §5.2 are not configurable, and
   no configuration or extension can weaken them.
2. **Decisions follow structured signals.** Control flow is decided only by stop reasons, validated
   structured output, tool calls and check results. The system never interprets free text to
   decide what happens next.
3. **A small core; everything else is opt-in.** A capability that is off adds no cost, no latency
   and no required settings. The simplest agent needs only a model profile and instructions.
4. **Patterns are composed from one primitive.** Every loop pattern, including a team, is built
   from the same single-agent loop. Budgets, cancellation, events and handoffs therefore behave
   the same everywhere.
5. **Guarantees are enforced, not requested.** A rule that must always hold is enforced by a gate,
   a sandbox, a check or a startup validation, never by an instruction in the prompt.
6. **Agents act within the owner's authority.** Permissions only ever narrow: owner → agent
   definition → agent → helper.
7. **Done is verified, not claimed.** Where a check is configured, only the check decides whether
   work is acceptable.
8. **Durable state lives outside conversations.** Records, tasks and memory survive shortening,
   restarts and agents ending.
9. **Every turn ends in a result.** A completion, a handoff or a rejection. Never a crash.
10. **Humans stay in control.** Wherever a human takes part, they can see, pause, redirect, approve
    and stop.
11. **The core knows no application.** No business concepts, no per-application branches. Presets
    and reference applications are consumers of the core, not part of it.
12. **Failures that make no noise are tested.** Each has a mandatory automated check.
13. **The simplest thing that works.** Each slice builds only what its acceptance criteria need: no
    abstractions without a current user, no speculative generality, and no optimization without a
    measured target (LAT, SCALE).

---

## 4. Conceptual model

### 4.1 Layers

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  APPLICATION     configuration · presets · extensions (own tools, checks, …) │
├──────────────────────────────────────────────────────────────────────────────┤
│  OPTIONAL CAPABILITIES (off by default)                                      │
│   conversation store · knowledge retrieval · human interaction · checkpoints │
│   team · task board · workspace · sandbox · project memory                   │
├──────────────────────────────────────────────────────────────────────────────┤
│  LOOP PATTERNS    single call · tool loop · workflow · router · fan-out ·     │
│                   evaluate-and-revise · plan-and-execute · team · custom      │
├──────────────────────────────────────────────────────────────────────────────┤
│  CORE (always present)                                                       │
│   single-agent loop · model access · tools and gates · context building ·    │
│   run record · admission · output checks · handoff · budgets · events ·      │
│   audit · invariants                                                         │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 4.2 What configuration describes

| Area | What can be configured |
|---|---|
| Models | Providers, model profiles, settings per profile, fallbacks, which profile each model slot uses, prices |
| Tools | Tool sets per model slot; per-tool limits, trimming, retries, approval, parallel safety, irreversibility; where tools come from |
| Loop pattern | Which pattern, its steps, how steps connect, stop conditions, nesting |
| Context | Which input sections are used, knowledge sources, history strategy, memory scope |
| Output | Free text or schema-validated structure, citation rules, output checks |
| Policies | Permission rules, gates, admission checks, masking, rate limits |
| Budgets | Limits at run, agent, task and turn level |
| Triggers | Interactive, single request, batch, schedule, event, long-running run |
| Capabilities | Which optional capabilities are on, and their settings |
| Operations | Storage, retention, events, logging and tracing |

### 4.3 Extension points

Where configuration is not enough, an application supplies an extension. Extensions plug in at
these points only:

| Extension point | Used for |
|---|---|
| Model provider | Adding a provider the core does not ship with |
| Tool | The application's own tools |
| Gate, admission check, output check, verification check | Rules that need code |
| Knowledge source | Retrieval from the application's own data |
| Loop pattern | A new pattern built from the core's primitives |
| Human interaction | How approvals, questions and messages reach a human |
| Event consumer | Displaying or forwarding events |
| Storage | Where runs, records, conversations, tasks, memory, checkpoints and audit entries live |
| Masking, history shortening, workspace, sandbox | Replacing the built-in behaviour |

### 4.4 Lifecycle of one agent turn (the primitive)

```
Work + caller identity
   │
   ▼
ADMISSION     masking (if on) → admission checks ──reject──► Rejected
   │
   ▼
PREPARE       task or request · retrieved knowledge · findings
   │
   ▼
┌─► BUILD INPUT   stable prefix ║ history (+ pending messages) ║ volatile context
│      │
│      ▼
│   CALL MODEL    ──► streamed content + stop reason + usage
│      │
│      ▼
│   DECIDE by structured signal ── finished ─────────────────────► OUTPUT CHECKS
│      │                        ── truncated / refused / unknown ─► handoff
│      │ wants tools
│      ▼
│   RUN TOOLS (in parallel where safe) — for each call:
│      validate → permission rules → gates → [human approval] → run as the caller
│      → trim result → record facts → audit → events
│      │
│      ▼
└── stop condition met? budget left? progress made? ── no ──► handoff

OUTPUT CHECKS (in order) → result, or revise (if the pattern allows), or handoff
```

---

## 5. Configuration (CFG)

### 5.1 The configuration model

| ID | Pri | Requirement |
|---|---|---|
| CFG-01 | MUST | Every agent is fully described by an agent definition: model profiles, instructions, tool sets, loop pattern, context strategy, output format, budgets, policies, triggers and enabled capabilities. |
| CFG-02 | MUST | Everything that does not need new behaviour can be configured without code. It can be written as configuration files or built programmatically, with the same expressiveness. |
| CFG-03 | MUST | The minimum valid agent definition is its instructions; it then uses the default model profile. Every other setting has a default (CFG-16). |
| CFG-04 | MUST | Configuration is layered: code defaults < preset < application < environment < run < agent. The effective configuration of any agent can be displayed, with where each value came from. A value that comes from a code default says so, and names the core version. |
| CFG-05 | MUST | Definitions can be reused and extended: one definition can build on another and override parts of it. |
| CFG-06 | MUST | Configuration is validated in full before anything runs. Missing references (models, tools, knowledge sources, extensions), incompatible combinations, missing capabilities and attempts to weaken invariants are all rejected, each with a message naming the setting and the fix. |
| CFG-07 | MUST | Configuration is versioned. Each run records the exact configuration it used, so its behaviour can be reproduced and explained. |
| CFG-08 | MUST | Configuration changes apply to new runs without rebuilding the application. During a run, only settings explicitly marked as live may change (for example, permission mode, owner messages, and budget increases by the owner). |
| CFG-09 | MUST | Secrets are referenced by name, as `{ "secret": "NAME" }`, and resolved from a secret source when used. Configuration should not hold secret values; the core does not scan configuration for them. |
| CFG-10 | MUST | Configuration contains no general-purpose scripting. Behaviour that needs logic is an extension (§4.3), which configuration refers to by name. |
| CFG-13 | MUST | Branch conditions and router mappings use a fixed, documented condition language over structured values only: field access, equality, membership in a list, numeric comparison, presence, and `and` / `or` / `not`. It has no variables, loops, functions or side effects. Anything more is a check or gate extension. |
| CFG-14 | MUST | Instructions may contain placeholders. Placeholders in the stable prefix may be filled only from definition-level or project-level values. Values that depend on the caller, the work or the current time are placed in the volatile context instead; a placeholder that would put them in the stable prefix fails validation (CTX-02). |
| CFG-15 | MUST | Configuration files are JSON. The core publishes a JSON Schema for them, generated from the Options classes (CFG-16), so editors can validate and complete them; full validation is still CFG-06. Other formats may follow after v1. |
| CFG-16 | MUST | Every setting's default is defined in code, as the initial value of a property in a plain Options class of the core. The Options classes are the single source for defaults, for the JSON Schema (CFG-15) and for the settings reference (DOC-01). The core itself does not depend on a configuration framework: binding files, environment variables and command-line options to the Options classes happens in the host application (the CLI). |
| CFG-17 | MUST | A new application must specify only: the instructions of each agent; a gate or a gate exemption with a reason for each write tool (INV-04); and the settings an application tool, gate, check or knowledge source declares as required. With the coding team preset, only the project's build and test commands are required, and the CLI detects them where it can. Every default is the safe choice: network off, permission mode `ask`, approval for irreversible tools, no helper agents. |
| CFG-11 | MUST | The core ships presets for: coding team, tool-using assistant, and single-call extractor. Presets for Q&A assistant, workflow, router, evaluate-and-revise and plan-and-execute follow after v1. *Reason: in v1 these patterns are covered by the runnable example configurations of TEST-06.* |
| CFG-12 | SHOULD | A configuration can be dry-run: validated, displayed, and executed against scripted models with no real model calls. |

### 5.2 Invariants (not configurable)

| ID | Pri | Requirement |
|---|---|---|
| INV-01 | MUST | Control flow follows only structured signals (principle 2). |
| INV-02 | MUST | Tools run with the caller's identity. Permissions only narrow along owner → definition → agent → helper. |
| INV-03 | MUST | Identity and permissions never come from model output, tool arguments, agent messages or content. |
| INV-04 | MUST | Every write tool has a gate of its own, or an explicit exemption with a stated reason (TOOL-02). |
| INV-05 | MUST | Every write-tool attempt is audited by the system. |
| INV-06 | MUST | Secrets known to the system (resolved from the secret source, or declared in configuration as secret) never reach model input, history, logs, events, handoffs or errors. Secret-like content the system was not told about, such as a credentials file inside the workspace, is protected by workspace rules (WS-05) instead. |
| INV-07 | MUST | Every turn ends in a result; budgets always exist, even if set very high. |
| INV-08 | MUST | Content from tools, documents and other agents is always labelled with its source and delimited as data, never placed where instructions go. Labelling reduces, but does not prevent, a model following planted instructions; the guarantee against their effects is SEC-01, enforced by permissions and gates. |
| INV-09 | MUST | Where a check is configured for work, the work is not accepted unless the check passes. |
| INV-10 | MUST | No agent can change its own definition, permissions, budget, gates, rules or checks. |

---

## 6. Core requirements

### 6.1 Conversation content (MSG)

| ID | Pri | Requirement |
|---|---|---|
| MSG-01 | MUST | A message has a role (user or assistant) and at least one piece of content. |
| MSG-02 | MUST | Content may be text, images, documents, a tool request, a tool result, or model reasoning. Model reasoning is kept exactly as received and sent back unchanged when the provider requires it. Provider-specific content the core does not understand is carried through unchanged. |
| MSG-03 | MUST | Messages cannot be altered after they are created. |
| MSG-04 | MUST | Content is labelled with its source: a human, an operator, another agent, the system or a tool. Only the owner and operators can give instructions. |
| MSG-05 | MUST | The system distinguishes these stop reasons: finished, wants tools, output limit reached, stop sequence, paused, refused, input too long, and unknown. |
| MSG-06 | MUST | Token usage is tracked separately for input, output, cache reads and cache writes, and converted to cost using configured prices. |
| MSG-07 | MUST | A tool request whose arguments do not match the tool's input format is reported as invalid arguments. |

### 6.2 Models (MDL)

| ID | Pri | Requirement |
|---|---|---|
| MDL-01 | MUST | The core is independent of any model provider. Providers are plugged in, and more than one can be used in the same application or run. |
| MDL-02 | MUST | A model profile configures: provider, model, reasoning effort, maximum output length, tool-choice mode, and any provider setting the provider declares, such as temperature, where supported. |
| MDL-03 | MUST | Model profiles are assigned per model slot: per agent definition and, within a loop pattern, per step. Each model slot uses the definition's default tool sets unless it sets its own (TOOL-03). For example, a cheap model can classify with no tools and a strong model can answer with search tools. |
| MDL-04 | MUST | A profile can list fallbacks: other profiles to use, in order, when the primary is unavailable or overloaded. A fallback is offered the same tools as the slot it serves; a fallback that cannot support them fails validation. Using a fallback is recorded. |
| MDL-05 | MUST | Provider failures are classified as transient, rate-limited, invalid request, authentication, or input too long. Transient and rate-limited failures are retried per configuration before the core sees a failure. |
| MDL-06 | MUST | Each provider declares what it supports (e.g. reasoning, structured output, images, provider-side history shortening, provider-side tools, batch processing). Configuration that requires a missing capability fails validation. |
| MDL-07 | MUST | Model output is streamed as it is generated, with no change to how the turn is decided. |
| MDL-08 | MUST | All agents in a process share each provider's rate limits fairly. No agent is starved, and coordinating agents take priority. |
| MDL-09 | MUST | Prices per model are configurable, so cost is reported correctly for any provider. |
| MDL-10 | SHOULD | Work that does not need an immediate answer (batch trigger) can use the provider's lower-cost batch processing when it offers one. |

### 6.3 The single-agent loop (LOOP)

| ID | Pri | Requirement |
|---|---|---|
| LOOP-01 | MUST | Each agent has its own conversation and loop. Many agents can run at once. |
| LOOP-02 | MUST | Turns for the same agent are processed one at a time. Work that arrives during a turn waits; it is not rejected. |
| LOOP-03 | MUST | The next step depends only on the stop reason: wants tools → run tools; finished or stop sequence → evaluate stop conditions; paused → call again; output limit → handoff (truncated output); refused → handoff (provider refusal); input too long → shorten history (HIST-04); unknown → handoff. |
| LOOP-04 | MUST | A "wants tools" stop with no tool request ends in a handoff. |
| LOOP-05 | MUST | Stop conditions are configurable: the model finishes (default); a designated "finish" tool is called; the output passes its checks; a maximum number of iterations is reached. They can be combined. |
| LOOP-06 | MUST | Each turn has a budget (iterations, tool calls, tokens, cost, time), checked before every model call. When it runs out, the turn ends in a handoff carrying everything established so far. Default: 50 iterations. |
| LOOP-07 | MUST | The system detects a stalled turn. An iteration counts as progress if any of these holds: the run record accepts a new fact, finding or decision; a working copy or workspace changes; a tool is called with arguments not used before in this turn; or a repeated call (same tool, same arguments) returns a result different from its previous result. An iteration that only repeats earlier calls and gets the same results makes no progress. A configurable number of consecutive iterations without progress (default 3) ends the turn in a handoff. Exploration (reading new files) and edit–test–edit cycles are therefore progress. |
| LOOP-08 | MUST | Tool calls from one model response run concurrently when every tool involved is marked safe to run in parallel, up to a configurable limit. Results go back to the model together, in the order requested. |
| LOOP-09 | MUST | A turn can be cancelled at any point. Recorded facts are kept. Every tool request gets a matching result, marked cancelled where necessary. |
| LOOP-10 | MUST | The model input is rebuilt before every model call, so new facts and messages are visible to the next iteration. |
| LOOP-11 | MUST | If every tool call in an iteration is refused for lack of permission, the turn ends in a handoff (policy gap). Configurable. |
| LOOP-12 | MUST | When a tool call needs human approval, only that agent waits, then continues the same turn. |

### 6.4 Loop patterns (PAT)

| ID | Pri | Requirement |
|---|---|---|
| PAT-01 | MUST | The core provides these patterns, selectable by configuration: **single call** (one model call, no tools); **tool loop** (the single-agent loop; the default); **workflow** (a fixed sequence of steps, with optional conditional branches); **router** (a step classifies the work and a mapping picks the next step); **fan-out** (the same or different steps run in parallel over inputs, then results are combined); **evaluate and revise** (generate, check, revise until checks pass or a limit is reached); **plan and execute** (one step writes a plan, others carry it out, re-planning when a step fails); **team** (a lead coordinates agents over a task board — §8). |
| PAT-02 | MUST | Patterns nest: any step can itself be any pattern. For example, a workflow step can be a team, and a team member can use evaluate-and-revise. |
| PAT-03 | MUST | Branching and routing between steps use only structured signals: validated structured output, check results, tool calls or stop reasons. A mapping that does not cover a value ends in a handoff, never a guess. |
| PAT-04 | MUST | Data passes between steps through declared inputs and outputs. A step receives only what is passed to it and what its configuration allows it to read. |
| PAT-05 | MUST | Combining fan-out results is configurable: collect all, first to succeed, majority vote on structured output, or a combining step. The number of parallel branches is limited. |
| PAT-06 | MUST | Every pattern obeys the same rules as the primitive loop: budgets (drawn from its parent), cancellation, events, audit, handoffs and invariants. |
| PAT-07 | MUST | An application can register its own pattern as an extension, built from the same primitives, and select it by name in configuration. |
| PAT-08 | MUST | Every step reports its outcome (completed, handed off, failed, cancelled). A pattern defines what happens on each outcome: continue, retry (with a limit), take another branch, or hand off. |

### 6.5 Context (CTX)

| ID | Pri | Requirement |
|---|---|---|
| CTX-01 | MUST | The model input is built from three parts, in this order: **1 stable prefix**: tool descriptions, instructions, policies, then project memory · **2 conversation history**: each turn's work and the messages received during it (CTX-08) are kept here, labelled with their source · **3 volatile context**: facts, retrieved knowledge (before-the-turn retrieval), findings and decisions, and the current task's status and acceptance criteria. Each section can be switched off; the order cannot change. |
| CTX-02 | MUST | Nothing that depends on the caller, the work, the conversation or the current time appears in the stable prefix. A violation is rejected before the model is called. |
| CTX-03 | MUST | Agents with the same definition, model slot and memory scope share an identical stable prefix, so they share the provider's cache. Tools are always presented in the same order. |
| CTX-10 | MUST | History is **append-only**: nothing already sent to the model is edited, reordered or removed, except by history shortening (HIST). Editing earlier content breaks the cache from that point on, and on current Claude models it also invalidates the model's earlier reasoning. The volatile context is therefore delivered without editing earlier content. Where the provider offers turn-scoped content, which is shown for one model call and then cleared by the provider, a fresh copy is sent with each call. Otherwise the volatile context is appended after the newest content and kept, and later calls in the same turn append only what changed since it was last sent. |
| CTX-11 | MUST | The core places cache boundaries at: **(a)** the end of the part of the prefix shared by every agent of the definition (tools, instructions, policies); **(b)** the end of project memory; and **(c)** the last cacheable block of history, so each model call reuses the cache for everything the previous call sent. The number of boundaries never exceeds the provider's limit. Cache lifetime is configurable per boundary where the provider supports it; for example, a longer lifetime for agents that often wait for approvals. Where the provider requires it, boundaries with longer lifetimes come before boundaries with shorter ones. |
| CTX-04 | MUST | Retrieval is configurable as: none; **before the turn** (knowledge is fetched once and placed in the volatile context); or **as a tool** (the agent searches when it chooses). Both can be used together. |
| CTX-05 | MUST | A knowledge source returns passages, their citations, and whether the question is covered, partly covered or not covered. "Not covered" can end the turn in a handoff (policy gap). Configurable. |
| CTX-06 | MUST | History strategy is configurable: **none** (each request stands alone); **full**; **shortened** when long (HIST); or **last N turns**. |
| CTX-07 | MUST | Facts are presented in a consistent order, each with its as-of time. Building the input reads the run record but never changes it. |
| CTX-08 | MUST | Messages that arrive during a turn (from the owner, an operator or another agent) are appended to history at the next iteration, labelled with their sender, and kept there. |
| CTX-09 | SHOULD | Applications can add operating facts, such as limits or the caller's capabilities, to every turn. |

### 6.6 Tools (TOOL)

| ID | Pri | Requirement |
|---|---|---|
| TOOL-01 | MUST | A tool can come from: the application; a built-in tool pack of the core (for example, file, search and command tools, available with the workspace and sandbox capabilities); the model provider's own server-side tools; or an external tool server using the Model Context Protocol. All are governed by the same permissions, gates, audit and events. Supported tool server transports: stdio and Streamable HTTP. |
| TOOL-02 | MUST | Tools are checked when the system starts: names are unique; input formats are valid; every write tool has at least one gate of its own beyond the permission check, unless it is explicitly exempted with a stated reason. |
| TOOL-03 | MUST | Tools are grouped into named tool sets. An agent definition lists default tool sets that apply to all its model slots; a slot may set its own tool sets instead (MDL-03). The tools offered to a model depend only on its slot's tool sets and the enabled capabilities, never on the caller or the permission mode, so the stable prefix stays cacheable. A call the caller is not permitted to make is denied at the permission check (TOOL-05) with a "not authorised" error. |
| TOOL-04 | MUST | Per tool, configuration sets: read or write, required permissions, gates, approval policy (never, always, or by rule), time limit, retries on failure, result trimming, whether it is safe to run in parallel, and whether its effects are irreversible. |
| TOOL-05 | MUST | Before a tool runs, checks happen in this order: argument validation, permission rules, gates for all tools, then the tool's own gates. The first one that does not allow the call decides the outcome: allow; deny (with a reason); ask a human; or route to another agent or a human. |
| TOOL-06 | MUST | Gates can read the run record and, when on, the task board, so they can require prerequisites. |
| TOOL-07 | MUST | All tool calls go through one path that applies gates, time limits, retries, error handling, audit and events. |
| TOOL-08 | MUST | Tool errors reach the model as a category, a short message, and whether retrying may help. Categories: unknown tool, invalid arguments, not authorised, policy violation, timeout, unavailable, failed. Internal details are logged, never shown to the model. |
| TOOL-09 | MUST | A tool result is trimmed as its configuration says before it enters the conversation. The full result is kept as an artifact the agent can page through. |
| TOOL-10 | MUST | A tool marked irreversible is carried out **at most once** for the same run, tool and arguments, including after a restart. Its intent is durably recorded before it runs. A restart that finds an intent with no recorded outcome never re-runs it; the call goes to a human (RUN-07). A stable idempotency key identifying the call is passed to the tool, so downstream systems can reject duplicates too. |
| TOOL-11 | MUST | Every write-tool attempt — allowed, denied, asked, failed or completed — is audited: run, agent, caller, tool, arguments, deciding rule or gate, outcome, time. |
| TOOL-12 | SHOULD | When an agent has many tools, their descriptions can be loaded on demand, so large tool sets do not fill the model input. |
| TOOL-13 | MUST | Provider server-side tools are run by the provider, so the per-call steps of TOOL-05 (gates, approval, running as the caller) cannot apply to them. They are governed by configuration only: each one is enabled explicitly, per tool set, with a stated reason and with the provider's own limits (such as maximum uses or allowed domains) where offered. Their calls and results are audited and published as events after the fact, and count towards budgets. |

### 6.7 Output (OUT)

| ID | Pri | Requirement |
|---|---|---|
| OUT-01 | MUST | Output format is configurable: free text, or structured output validated against a schema. |
| OUT-02 | MUST | Structured output that fails validation is returned to the model with the validation errors, up to a configurable number of attempts (default 2), then handed off. |
| OUT-03 | MUST | Output checks are configurable and run in order; the first failure wins. The outcome of a failure is configurable: hand off (default), or revise when the pattern supports it. |
| OUT-04 | MUST | Citation rules are configurable: **off**; **resolve** (every cited id must exist in the run record — the default when a knowledge source is configured); or **required** (every answer must cite at least one source, or hand off). |
| OUT-05 | MUST | A result can carry artifacts, such as files, reports or data, as well as text. |

### 6.8 Run record (REC)

| ID | Pri | Requirement |
|---|---|---|
| REC-01 | MUST | Each run has a run record, shared by its agents, containing facts (value, source, as-of time), findings, decisions (choice, reason, author, date, what it replaces) and citations (short id, document, location, quote, date). |
| REC-02 | MUST | Agents propose changes to the run record through record tools; they never write it directly. The core validates each proposal (format, sources, citations resolving) and applies it through one controlled update step. Each update is all-or-nothing, attributed to the proposing agent, and advances a revision number. A rejected proposal returns the reason to the agent. |
| REC-03 | MUST | Conflicting values or decisions from different sources or agents are both kept and reported. The system does not choose between them. |
| REC-04 | MUST | Simultaneous updates never overwrite each other silently. |
| REC-05 | MUST | The run record is never trimmed or shortened. |
| REC-06 | SHOULD | An agent can be limited to the parts of the run record relevant to its definition and work. |

### 6.9 Conversation history (HIST)

| ID | Pri | Requirement |
|---|---|---|
| HIST-01 | MUST | Shortening is the only operation that may change history already sent (CTX-10), and it follows the provider's rules for doing so. It is configurable: the provider's own mechanism (default where supported), an application extension, or off. |
| HIST-02 | MUST | Shortened history is checked before use: every tool request still has its result, the current turn is untouched, and history starts with a user message. |
| HIST-03 | MUST | Shortening never changes the run record, tasks or memory. It may add findings. |
| HIST-04 | MUST | If the provider reports the input is too long, history is shortened once. If that fails, the turn ends in a handoff. |
| HIST-05 | SHOULD | Old, bulky tool results can be cleared from history, keeping a short note of what they contained. |

### 6.10 Admission (ING)

| ID | Pri | Requirement |
|---|---|---|
| ING-01 | MUST | Work from outside the core passes through admission first: masking (if on), then admission checks in order. The first rejection wins. |
| ING-02 | MUST | Masking of personal data is **on by default**. When on, it runs before content reaches the model provider, the run record, stored history or logs. It masks email addresses, phone numbers and payment card numbers by default; the list is configurable. Masking can also be applied to tool results and retrieved content, configured per tool and per knowledge source. Applications whose content masking would corrupt, such as source code, can turn it off; the coding team preset turns it off. |
| ING-06 | MUST | Masking is reversible within a run. Each masked value is replaced by a token that is stable for the run. When a tool configured to receive real values is called with a token in its arguments, the core restores the value just before the tool runs. Restored values are never returned to the model, and never written to history or logs. |
| ING-03 | MUST | Rate limits are configurable per owner, per tenant and per run. |
| ING-04 | MUST | A rejection returns a normal result with a reason, not an error. |
| ING-05 | MUST | The caller's identity includes an identifier, a tenant, permissions and additional attributes. It contains no business-specific fields. Anonymous callers are supported, with the permissions configuration grants them. |

### 6.11 Results and handoff (EGR)

| ID | Pri | Requirement |
|---|---|---|
| EGR-01 | MUST | A result states whether the work completed, was handed off or was rejected. It includes the output, citations, artifacts, statistics (iterations, tool calls, tokens, cost, elapsed time), the transcript, and the handoff if there is one. |
| EGR-02 | MUST | A handoff can go to a human, a named agent definition, or the step that started the work. It contains the reason, the original work, facts, findings, decisions, citations, the tool calls attempted and their outcomes, any pending action, the agent's last text, statistics, and the transcript. It is built entirely from recorded state; no model is asked to summarise. |
| EGR-03 | MUST | Handoff reasons are: requested by a human, policy gap, no progress, budget exhausted, provider refusal, provider failure, truncated output, invalid structured output, approval denied or timed out, routed by a gate, output check failed, verification failed, no route for a value, work passed to the next agent. Self-reported confidence and sentiment are never reasons. |
| EGR-04 | MUST | A handoff to a human is raised only by an explicit signal: a flag on the request, a human's command, a gate, or a tool the model can call. Never by keywords in text. |

### 6.12 Triggers (TRG)

| ID | Pri | Requirement |
|---|---|---|
| TRG-01 | MUST | Work can reach an agent as: an interactive conversation; a single request with a single result; a batch of inputs; a schedule; an external event; or a long-running run. The trigger is configurable per agent definition. |
| TRG-02 | MUST | The same agent definition behaves the same whatever triggers it; only delivery changes. |
| TRG-03 | MUST | A batch reports a result per input. A failed input never stops the others. |
| TRG-04 | MUST | Single requests and batches can run with no stored conversation (stateless). |

### 6.13 Events (EVT)

| ID | Pri | Requirement |
|---|---|---|
| EVT-01 | MUST | The core publishes a live event stream: status changes of runs, agents, steps and tasks; streamed model text; tool calls starting and ending; approvals requested and answered; messages and handoffs; budget warnings; cost. Capabilities add their own events, such as file changes and command output. |
| EVT-02 | MUST | Every event identifies its run, agent and step. Events are in order for each agent. |
| EVT-03 | MUST | A consumer that joins late or reconnects can catch up from any earlier point in a stored run. |
| EVT-04 | MUST | A slow or failed consumer never slows or stops agents. |
| EVT-05 | MUST | Which events are stored, and for how long, is configurable. |

---

## 7. Capabilities (CAP)

| ID | Pri | Requirement |
|---|---|---|
| CAP-01 | MUST | Each of these capabilities can be switched on or off per application or agent definition: conversation store, knowledge retrieval, human interaction, checkpoints, team, task board, workspace, sandbox, project memory. All are off by default. |
| CAP-02 | MUST | A capability that is off adds no latency, no storage and no required settings, and its tools are not offered to the model. |
| CAP-03 | MUST | Dependencies between capabilities are checked when configuration is validated. For example, a team requires a task board; command tools require a sandbox. |
| CAP-04 | MUST | An application using none of the capabilities, such as a stateless single-call extractor, is fully supported. |
| CAP-05 | MUST | The conversation store keeps every agent's conversation persistently, so a conversation unloaded from memory or interrupted by a restart continues with its full history. It is required whenever history is kept across requests. |

---

## 8. Capability requirements

### 8.1 Long-running runs and checkpoints (RUN)

A **checkpoint** is a saved state of a run — run record, conversations, tasks, memory and
workspace — that it can resume from or roll back to.

| ID | Pri | Requirement |
|---|---|---|
| RUN-01 | MUST | A run has an outcome it works towards, an owner, a budget and a status: running, paused, waiting for a human, completed, failed or cancelled. With the team capability it also has a team, a task board and a planning status. |
| RUN-02 | MUST | A run can last from one request to many days, spanning many sessions and restarts. |
| RUN-03 | MUST | Checkpoints are taken at configurable points: after each turn (the default), after each step, after each integration, and on demand. |
| RUN-04 | MUST | After a crash or restart, a run resumes from its last checkpoint with no lost checkpointed work. Its working copies are restored to their checkpoint state first, and work done since the checkpoint is redone. |
| RUN-05 | MUST | Budgets form a hierarchy: run, agent, task (when on), turn. Each level can cap tokens, cost, time and tool calls. When a lower level runs out, the work goes to the level above. When the run budget runs out, the run pauses and asks the owner, or ends in a handoff when no human interaction is configured. Default run budget: $25 of cost and 8 hours of elapsed time. |
| RUN-06 | MUST | The owner can pause, resume and cancel the run or any single agent. Cancelling stops model calls and sandboxed processes within a configurable time (default 10 seconds). |
| RUN-07 | MUST | A tool call interrupted without a known outcome is flagged on resume. An irreversible one is never re-run automatically. |
| RUN-08 | MUST | The owner can roll a run back to any checkpoint, restoring its state and workspace together. Effects outside the core's state, such as calls to external systems and irreversible tool calls, are not undone; the rollback lists the ones made after the checkpoint. |
| RUN-09 | MUST | Everything that happened in a run can be reconstructed from its stored events, audit log and checkpoints. |
| RUN-10 | MUST | Cost is reported live and in total, broken down by agent, definition, task, step and model. |
| RUN-11 | SHOULD | When a long-running run ends, it produces a run report: outcome, work done, decisions, check results, cost and open issues. |
| RUN-12 | MUST | At most one run is active on a workspace at a time, enforced by a lock. Starting a second run on the same workspace is rejected with a message naming the active run. Runs on a workspace can follow one another. |

### 8.2 Team (TEAM)

| ID | Pri | Requirement |
|---|---|---|
| TEAM-01 | MUST | A team is configured as a set of roles (agent definitions) with the maximum number of agents of each role at once, and one lead. |
| TEAM-02 | MUST | The lead turns the goal into a plan and tasks, assigns them, reviews results, resolves conflicts and re-plans. The lead runs the same loop as every other agent. A human can take the lead's place. |
| TEAM-03 | MUST | Agents work in parallel, up to a configurable limit per run (default 4). |
| TEAM-04 | MUST | An agent never sees another agent's conversation. It receives only what is passed to it on purpose. |
| TEAM-05 | MUST | Agents interact only through: the task board, handoffs, direct messages, the run record and project memory. |
| TEAM-06 | MUST | A message between agents names its sender and recipient, is recorded, and is treated as data. |
| TEAM-07 | MUST | An agent may start helper agents only if its role allows it, within configurable depth (default 2) and count limits. A helper's permissions and budget come out of its parent's. |
| TEAM-08 | MUST | Each agent has a visible status: idle, working, waiting (for a dependency, an approval or a message), finished or failed. |
| TEAM-09 | MUST | An agent that fails, stalls or runs out of budget is stopped. Its work returns to the lead with the reason, to retry, reassign, split or escalate. |
| TEAM-10 | SHOULD | The owner can require approval of the lead's plan before work starts. |

### 8.3 Task board (TASK)

| ID | Pri | Requirement |
|---|---|---|
| TASK-01 | MUST | A task has an id, title, description, acceptance criteria, verification checks, the role it needs, an owner, a status, dependencies, a priority, a budget, artifacts and a history of attempts. |
| TASK-02 | MUST | Statuses are: proposed, ready, in progress, in review, blocked, done, failed and cancelled. Only configured status changes are allowed. |
| TASK-03 | MUST | A task becomes ready only when its dependencies are done. Circular dependencies are rejected. |
| TASK-04 | MUST | At most one agent works on a task at a time. |
| TASK-05 | MUST | A task can be marked done only when all its verification checks pass. |
| TASK-06 | MUST | A task can require review. The reviewer is never the author, and the outcome is recorded with its reasons. |
| TASK-07 | MUST | Every change to a task is recorded: who, when, what changed, and why. |
| TASK-08 | MUST | The owner can view the board and add, edit, reprioritise, reassign or cancel tasks at any time. |
| TASK-09 | MUST | A task that runs out of budget, or fails a configurable number of attempts (default 3), goes back to the lead. |

### 8.4 Workspace (WS)

A **workspace** holds the files agents work on. The **baseline** is its verified, integrated
version; each agent that changes files has its own **working copy**. The built-in workspace is
backed by git: the baseline is a branch, and each working copy is a separate worktree and branch.
It can be replaced through the workspace extension point.

| ID | Pri | Requirement |
|---|---|---|
| WS-01 | MUST | Each agent that changes files works in its own working copy. Its changes are invisible to other agents until integrated. |
| WS-02 | MUST | Changes are integrated into the baseline only after their verification checks pass, and only if the baseline's own checks still pass afterwards. |
| WS-03 | MUST | Conflicts are detected and turned into work for the author or the lead. They are never resolved silently and never overwrite other work. |
| WS-04 | MUST | Every change is versioned and attributed to a run, agent and task. |
| WS-05 | MUST | Agents can read and change files only inside the workspace. Configurable protected paths are read-only or hidden. |
| WS-06 | MUST | Agents can search files and read parts of a file, so large files never need to enter a conversation whole. |
| WS-07 | MUST | An edit states exactly what it replaces. It fails if the file changed since the agent last read it. |
| WS-08 | MUST | Working copies are cleaned up when their task ends, unless the owner keeps them. |
| WS-09 | MUST | Integrations go through one integration queue per workspace, in order. Each change is checked against the baseline as it is when its turn comes, not as it was when the work started. A change that no longer applies cleanly becomes a conflict (WS-03). The queue's length and waiting time are visible to the owner and the lead. |

### 8.5 Sandbox (SBX)

| ID | Pri | Requirement |
|---|---|---|
| SBX-01 | MUST | Commands run in a sandbox limited to the agent's working copy. Network access is off by default and can be allowed for listed destinations; allowed traffic passes through a filtering proxy that enforces the list. CPU, memory, time and output size are limited. |
| SBX-07 | MUST | The built-in sandbox meets SBX-01…06 on both Linux and Windows, using each platform's own isolation mechanism behind the sandbox extension point. Startup reports clearly if the machine cannot provide the required isolation; the core never falls back silently to running commands unsandboxed. |
| SBX-02 | MUST | Permission rules decide, by command and by path, whether an action is allowed, asked about or denied. Anything not covered is asked about. |
| SBX-03 | MUST | Background processes can be started, observed and stopped. They are stopped when their agent, task or run ends. |
| SBX-04 | MUST | Command output is streamed as events. The model receives a trimmed version; the full output is kept as an artifact. |
| SBX-05 | MUST | Secrets are available inside the sandbox only when the role allows it, and never reach the model, history or logs. |
| SBX-06 | MUST | Sandboxes of different agents cannot see or affect each other. |

### 8.6 Human interaction (HITL)

| ID | Pri | Requirement |
|---|---|---|
| HITL-01 | MUST | A permission mode is configurable per run: **ask** (confirm writes not covered by an allow rule), **auto** (act within the rules, ask only when a rule says so), or **read-only**. It can be changed during the run. |
| HITL-02 | MUST | When approval is needed, only the waiting agent pauses. The human can approve, deny, or approve a changed version, and the agent continues the same turn. A changed version goes through argument validation, permission rules and gates again before it runs. With no answer within a configurable time (default 30 minutes), the request is denied and the agent is told why. |
| HITL-03 | MUST | The owner can message any agent at any time. The agent receives it at its next iteration as an instruction from the owner. |
| HITL-04 | MUST | Sign-off points where a run waits for the owner are configurable, such as approving a plan, integrating changes, or exceeding a budget. The coding team preset turns on, by default: approving the lead's plan before work starts, exceeding the run budget, and any irreversible action. Integration into the baseline is not a default sign-off, because checks already decide it (WS-02). |
| HITL-05 | MUST | A run can continue with no human attached, within the permissions granted when it started. Questions and approvals queue while unaffected agents keep working. |
| HITL-06 | MUST | An agent can ask the owner a question and receive the answer in the same turn. |
| HITL-07 | MAY | End users can rate a result. The rating is linked to the run for later review. |

### 8.7 Project memory (MEM)

| ID | Pri | Requirement |
|---|---|---|
| MEM-01 | MUST | Project memory holds durable instructions, conventions, how to build and test, and an architecture summary. It is part of the stable prefix of the agent definitions configured to use it. |
| MEM-02 | MUST | Decisions that affect the whole project are recorded in memory with reason, author and date. A decision that replaces an earlier one says so. |
| MEM-03 | MUST | Agents can propose memory changes. A change is applied only when the lead or the owner approves it, as configured. The stable prefix of a running conversation is never edited: an approved change reaches running conversations as an appended operator message at their next turn, and conversations started afterwards have the updated memory in their prefix. |
| MEM-04 | MUST | Memory persists across runs. Its scope is configurable: per project, per owner, or per tenant. |
| MEM-05 | MUST | Memory has a size limit. When it is exceeded, it is condensed and the owner can review the result. Content is never dropped silently. |

---

## 9. Non-functional requirements

| ID | Pri | Requirement |
|---|---|---|
| SEC-01 | MUST | Instructions hidden in files, documents, command output or agent messages cannot make any agent act beyond its permissions. This does not stop planted instructions from misusing actions the agent *is* permitted, such as sending data out through an allowed network destination. That remaining risk is reduced by narrow permissions, gates and SEC-04. |
| SEC-02 | MUST | Tenants are isolated: no caller can see another tenant's conversations, records, memory, citations or artifacts. Separation is logical: everything stored carries its tenant id, and the storage layer enforces it on every read and write. |
| SEC-03 | MAY | The audit log is tamper-evident. |
| SEC-04 | SHOULD | The core marks an agent as having read untrusted content (for example fetched web pages, or configured sources). Gates and approval rules can use this mark, for example to require approval for writes to external destinations from a marked agent. |
| SEC-05 | MUST | Tools that act on external services use credentials bound to the owner (or tenant), resolved from the secret source when the tool runs. Credentials never appear in model input, tool arguments or agent messages. A helper agent uses its parent's credentials only within its narrowed permissions. |
| PRIV-01 | MUST | Retention is configurable per kind of data: conversations, run records, artifacts, events and audit entries. |
| PRIV-02 | MUST | All stored data belonging to an owner can be exported and deleted on request. The audit log keeps only what its retention rules require. |
| COST-01 | MUST | The stable prefix is identical, byte for byte, across agents of a definition that share a model slot and memory scope, and across their work items and turns, until project memory changes. History is identical, byte for byte, between consecutive model calls of a turn (CTX-10). Cache reads and writes are measured on every model call. A cache hit rate below a configurable threshold (default 70%) publishes a warning event. |
| COST-02 | MUST | Tool results are trimmed before entering a conversation, and every budget level is enforced. |
| REL-01 | MUST | Retries after rate limits, provider overload and server errors wait progressively longer, respect the provider's requested wait time, and stop after a configurable number of attempts. |
| REL-02 | MUST | No turn, agent or run ends in an unhandled error. A failing agent never brings down other agents or other runs. |
| REL-03 | MUST | With checkpoints on, a crash loses at most the work since the last checkpoint (RUN-03). Whatever the checkpoint settings, every write-tool attempt is durably recorded before it runs (its audit entry, INV-05), so a resumed run knows which effects may already have happened (RUN-07, TOOL-10). |
| REL-04 | MUST | Stored runs, checkpoints, records, tasks and memory carry a format version. A core release resumes runs created by the previous minor release, or refuses with a message naming the incompatibility. It never resumes from state it has misread. |
| CONC-01 | MUST | Concurrent agents cannot corrupt shared state: run record, tasks, memory, working copies. Separate runs never share mutable state. |
| SCALE-01 | SHOULD | One process supports at least 1,000 concurrent short conversations, with idle conversations unloaded and restored on demand. *Deferred reason: this serves the document Q&A application, which follows after v1. It becomes a MUST then.* |
| SCALE-02 | MUST | One run supports at least 8 agents working at once and 500 tasks on one developer machine. |
| SCALE-03 | SHOULD | A run of 48 hours or more keeps stable memory use and response times. |
| LAT-01 | MUST | Excluding model time, tool time and durable storage writes, the core adds less than 5 ms per iteration at the 95th percentile. Storage write latency is measured separately and reported by the load tests (TEST-30). |
| LAT-02 | MUST | For interactive triggers, the first streamed text reaches the caller as soon as the provider sends it. The core adds less than 50 ms. |
| OBS-01 | MUST | Runs, agents, steps, tasks, turns, model calls, tool calls and gate decisions are traced, exportable through OpenTelemetry, with consistent attribute names. |
| OBS-02 | MUST | Metrics are collected for tokens and cost (by model, definition, agent, task and step), iterations, tool calls by outcome, handoffs by reason, check pass rates, fallbacks used, and latency. |
| OBS-03 | MUST | Logs are structured and identify the run, agent, step and turn. Conversation content is not logged by default. |
| UX-01 | MUST | Where a human takes part, they can see at a glance what each agent is doing, what is waiting for them, and the cost so far. |
| DOC-01 | MUST | Every configuration setting is documented with its meaning, allowed values, default and an example. The reference is generated from the Options classes (CFG-16), so it cannot drift from the defaults in code. |
| STO-01 | MUST | Storage is configurable through the storage extension point. The core ships two implementations: **in-memory** (used by the test kit) and a **default local storage**: an embedded single-file database (SQLite) for runs, records, conversations, tasks, memory, checkpoints, events and audit, plus files on disk for artifacts. The coding team CLI uses the default local storage, kept in the project directory unless configured otherwise. |

---

## 10. Claude API integration (CLD)

| ID | Pri | Requirement |
|---|---|---|
| CLD-01 | MUST | The core works with the Claude API. Every Claude model setting is available through model profiles. |
| CLD-02 | MUST | Translating between the core's format and the Claude API format can be verified without a network connection. |
| CLD-03 | MUST | Each cache boundary (CTX-11) is actually sent to the Claude API as a cache marker, with its configured lifetime. The prefix maps to the API's order: tools (fixed order), then system (instructions, policies, project memory). History maps to messages. Where the model supports them, the volatile context is sent as a turn-scoped mid-conversation system message (one that is cleared at the next user message), and operator messages, including memory changes, are sent as mid-conversation system messages. Otherwise the volatile context is sent as a text block after the tool results in the final user message (CTX-10). |
| CLD-04 | MUST | Every Claude stop reason is recognised. An unrecognised one is reported as unknown, not as an error. |
| CLD-05 | MUST | Claude's reasoning output is preserved and returned exactly as the API requires. |
| CLD-06 | MUST | Claude's structured output, server-side tools, server-side history shortening, clearing of old tool results, task budgets and server-side refusal fallback can each be switched on in configuration. |
| CLD-07 | MUST | Token usage, including cache reads and writes, is captured and priced. |
| CLD-08 | MUST | Claude API errors are classified into the categories in MDL-05. |
| CLD-09 | MUST | Streaming is supported. |
| CLD-10 | MUST | All agents in a process share one view of the account's rate limits. |
| CLD-11 | SHOULD | Claude's batch processing is used for the batch trigger when configured. |
| CLD-12 | MUST | The Claude provider is built on the official Anthropic C# SDK wherever the SDK supports the needed feature. The SDK is used only inside the provider implementation: none of its types appear in the core's public interfaces, configuration or stored data, and replacing it affects only the provider. No other AI or agent framework is used anywhere in the core or the provider. |

---

## 11. Verification (TEST)

**Configuration and core**

| ID | Pri | Requirement |
|---|---|---|
| TEST-01 | MUST | A test kit runs any configuration against scripted models, a controllable clock, fake tools, an in-memory workspace and a fake sandbox, with no network or API key. |
| TEST-02 | MUST | Real model exchanges can be recorded and replayed offline. |
| TEST-03 | MUST | Every stop-reason outcome and every stop condition is tested. |
| TEST-04 | MUST | Invalid configurations are shown to be rejected with a message naming the setting: missing references, missing capabilities, unmet dependencies, and each attempt to weaken an invariant (INV-01…10). |
| TEST-05 | MUST | The effective configuration is shown to report where each value came from (CFG-04). |
| TEST-06 | MUST | Each built-in loop pattern (PAT-01) has a runnable example configuration, tested offline, including one nested pattern. |
| TEST-07 | MUST | A router with an unmapped value, and structured output that stays invalid, are each shown to end in a handoff. |
| TEST-08 | MUST | A capability that is off is shown to add no tools, no storage and no required settings. |
| TEST-09 | MUST | **Cache stability:** the stable prefix is shown to be identical for two agents of a definition, two work items and two turns, and for two callers with different permissions. Every model call is shown to only append to the content of the previous call; no earlier content is edited, reordered or removed, except by shortening. A memory change is shown not to edit the prefix of a running conversation. **Cache markers:** the provider request is shown to carry a marker at each boundary (CTX-11), within the provider's limit. |
| TEST-10 | MUST | **Gated writes:** a write tool with no gate of its own is shown to fail validation. A denied call is shown not to run and to be audited. |
| TEST-11 | MUST | **Injection:** instructions planted in a file, a document, command output, a tool result and an agent message are each shown to be unable to exceed permissions. |
| TEST-12 | MUST | An irreversible call is shown not to run twice, including after a crash and resume. A crash between recording the intent and recording the outcome is shown to send the call to a human, not to re-run it. |
| TEST-13 | MUST | Errors shown to the model are shown not to contain internal details or secrets. |
| TEST-14 | MUST | Budget exhaustion at each level, and stalled turns, go to the right place. An edit–test–edit cycle, and reading a series of different files, are each shown not to count as a stall. Repeating an identical call with an identical result is shown to count. |
| TEST-15 | MUST | Records, tasks and memory are shown to survive history shortening. Invalid shortened history is shown to be rejected. |
| TEST-16 | MUST | Model fallback is shown to be used when the primary is unavailable, and to be recorded. |
| TEST-17 | MUST | Tools from the application, the built-in packs and external tool servers are shown to be governed by the same permissions, gates and audit. Provider server-side tools are shown to require explicit enabling, and to be audited and counted towards budgets (TOOL-13). |
| TEST-18 | MUST | Tenant isolation, retention and deletion on request are each shown to work. |
| TEST-19 | MUST | Masked values are shown to reach only tools configured to receive them, and never to return to the model, history or logs (ING-06). |

**Capabilities**

| ID | Pri | Requirement |
|---|---|---|
| TEST-20 | MUST | A task is shown not to reach "done" while a verification check fails, whatever the agent says. A reviewer is shown never to be the author. |
| TEST-21 | MUST | Tasks are shown not to start before their dependencies. Circular dependencies are shown to be rejected. |
| TEST-22 | MUST | Two agents editing the same file are shown to produce a detected conflict. Integration that breaks the baseline's checks is shown to be rejected. |
| TEST-23 | MUST | **Crash and resume:** a run killed at random points is shown to resume with no lost completed work and no repeated irreversible effect. |
| TEST-24 | MUST | Rolling back to a checkpoint is shown to restore state and workspace together. |
| TEST-25 | MUST | On both Linux and Windows, the sandbox is shown to block file access outside the working copy, disallowed network access, and processes over their limits. |
| TEST-26 | MUST | An agent is shown to be unable to change its own or another agent's definition, permissions, budget, rules or checks. |
| TEST-27 | MUST | An approval pauses only the waiting agent, and the agent continues the same turn. Cancelling a run stops everything within the configured time. |
| TEST-28 | MUST | Events are shown to be ordered per agent and catchable-up after a reconnect. A stalled consumer is shown not to slow agents. |
| TEST-29 | MUST | **Team simulation:** a scripted team takes a small goal through plan, parallel work, review, integration, a forced restart and a report, entirely offline. This is the deterministic acceptance test for the team engine. |

**Quality and scale**

| ID | Pri | Requirement |
|---|---|---|
| TEST-30 | MUST | Scale and latency targets (SCALE-01, SCALE-02, LAT-01, LAT-02) are measured by automated load tests. |
| TEST-31 | MUST | **Coding team benchmark:** a fixed set of at least 10 development goals, in three tiers: about 4 small (for example a CLI tool or a single-file library), about 4 medium (for example a REST API with storage, around 15 files) and about 2 larger. Each is a one-paragraph goal with a hidden acceptance test suite written before M6, runs against the live model. Each goal runs at least 3 times from an empty workspace, and every run includes one forced restart. It tracks success rate, cost, time and human inputs needed. A run succeeds when the baseline passes all its own checks and the hidden acceptance tests, with human input only at the configured sign-off points. |
| TEST-32 | MUST | The core is shown to have no dependency on any model provider, provider SDK, AI or agent framework, or application. A dependency check, run as a test in CI, fails if: any project other than the Claude provider references the Anthropic C# SDK directly; `Microsoft.Extensions.AI.*` reaches any project other than transitively through that SDK; any project code references `Microsoft.Extensions.AI` types; or any other AI or agent framework package appears. |
| TEST-33 | SHOULD | Automated tests cover at least 85% of the core. |

---

## 12. Non-goals

v1 does not include:

- a graphical user interface;
- running one team across several machines;
- agent-to-agent protocols across a network;
- general-purpose scripting inside configuration;
- workflow automation that involves no model;
- real-time voice;
- resolving merge conflicts without an agent or a human;
- document indexing or search infrastructure;
- agents rewriting their own instructions, definitions or rules;
- escalation based on the model's self-reported confidence or on anyone's sentiment;
- undoing effects outside the core's state (external systems, irreversible calls) on rollback;
- the document Q&A reference application, which follows after v1;
- several runs working on the same workspace at the same time (RUN-12);
- any AI or agent framework other than the Anthropic C# SDK inside the Claude provider.

---

## 13. Milestones, acceptance, decisions and open questions

| # | Milestone | Done when |
|---|---|---|
| M0 | Design review | Spikes S00a and S00b are done; this specification, the configuration model and the design are approved |
| M1 | Configuration and foundations | Slices S01–S02 are done (`docs/plan/`); a minimal definition runs against a scripted model |
| M2 | Single agent | Slices S03–S10 are done; a tool-using agent runs fully offline |
| M3 | Claude integration | Slices S11–S12 are done; recording and replay work; cache hit rates are measured live |
| M4 | Loop patterns | Slice S13 is done; TEST-06 and TEST-07 pass |
| M5 | Coding agent | Slices S14–S17 are done on Linux and Windows; one agent changes, builds and tests a project from the CLI |
| M6 | Team and long-running | Slices S18–S20 are done; TEST-20…29 pass; the TEST-31 benchmark set and its hidden acceptance tests are written |
| M7 | Hardening | Slice S21 is done; TEST-30 passes; the TEST-31 benchmark meets its target |
| after v1 | Document Q&A | The Q&A reference application runs live with only the conversation store and knowledge retrieval capabilities on, with no changes to the core; SCALE-01 becomes a MUST |

**v1 is accepted when:**

- every MUST is met and verified by at least one test or a recorded review;
- all tests except TEST-31 pass with no network and no API key;
- every built-in loop pattern and preset runs from configuration alone;
- the **team simulation** (TEST-29) passes offline, deterministically;
- the **coding team benchmark** (TEST-31) reaches a success rate of at least **90%** across all its
  runs, on Linux and Windows;
- the core does not depend on any model provider, provider SDK or application.

**Decisions (revision 2)**

1. The coding team CLI is the primary v1 target. Document Q&A follows after v1.
2. The Claude provider uses the official Anthropic C# SDK, confined to the provider implementation (CLD-12). Its maturity was checked on 2026-09-30: GA since v10, v12.51.0 released 2026-09-28, roughly weekly releases, 5.2M downloads, MIT licence. Its transitive `Microsoft.Extensions.AI.Abstractions` dependency is accepted inside the provider only. No other AI or agent framework is used, and none is adapted to: not Microsoft Agent Framework, and not `Microsoft.Extensions.AI`.
3. Tools are configured once per agent definition for all its models, and a model slot may set its own instead. The offered tools never depend on the caller (TOOL-03).
4. The input is laid out as stable prefix, then history, then volatile context, with up to three cache boundaries. History is append-only (CTX-01, CTX-10, CTX-11).
5. Checkpoint frequency is configurable (RUN-03, default after each turn). Write-tool attempts are always recorded before they run (REL-03).
6. Storage is configurable; the default local storage is SQLite plus files (STO-01).
7. Supported platforms are Linux and Windows.
8. The core is an in-process .NET library.
9. Configuration files are JSON with a published JSON Schema (CFG-15).
10. Every setting has a default in code, in Options classes. A new application specifies only the few settings without a safe default (CFG-16, CFG-17).
11. v1 presets are coding team, tool-using assistant and single-call extractor (CFG-11).
12. Default sign-offs for the coding team are plan approval, exceeding the run budget, and irreversible actions (HITL-04).
13. Default limits:

    | Limit | Default | Requirement |
    |---|---|---|
    | Run budget | $25, 8 hours | RUN-05 |
    | Turn budget | 50 iterations | LOOP-06 |
    | Approval timeout | 30 minutes, then deny | HITL-02 |
    | Cache-hit warning | below 70% | COST-01 |
    | Concurrent agents | 4 | TEAM-03 |
    | Task attempts | 3 | TASK-09 |
    | Helper depth | 2 | TEAM-07 |
    | Stall | 3 iterations without progress | LOOP-07 |
    | Cancellation | within 10 seconds | RUN-06 |

14. One active run per workspace (RUN-12).
15. Tenant separation is logical, enforced by the storage layer (SEC-02).
16. The external tool server transports are stdio and Streamable HTTP (TOOL-01).
17. The benchmark goals are tiered small, medium and larger, and the 90% target applies across the whole set (TEST-31).
18. The coding team trusts the sandbox: its command rules deny `git push` and `git remote` and allow every other command,
    as the sandbox is the boundary and asking about each command added friction, not safety (SBX-02, 2026-10-04).

**Open questions**

None at revision 2. Raised during the build, for the owner to decide at revision 3:

- Readings of three MUSTs, which [`verification.md`](docs/plan/verification.md) records: CFG-01, CAP-01 and TASK-02.
- Proposed as not in v1, with reasons in [S21](docs/plan/S21-hardening.md): CLD-11 (SHOULD), HITL-07 (MAY) and SEC-03 (MAY).
- §4.3 lists masking as replaceable; the design keeps it in the core, not replaceable in v1.
- No MUST gap is left: CFG-17's command detection is `sof init`. [`verification.md`](docs/plan/verification.md) lists
  only TEST-31, the live benchmark run, as pending.
