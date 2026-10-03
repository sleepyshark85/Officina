# Officina — Configuration Reference

How the configuration fits together: value forms, the condition language, patterns, merging, validation and presets.
Start with [`CONFIGURATION.md`](../CONFIGURATION.md), which lists the few settings a new application must specify. Every
setting, with its default and an example, is in the generated [settings reference](configuration-settings.md) and the JSON
Schema [`officina.schema.json`](officina.schema.json) (CFG-15, DOC-01). Where this page and the settings reference differ,
the settings reference is right: it is generated from the code.

**Not built in v1.** For the open MUSTs below, §17 keeps the spec. The rest wait for a known case (principle 13) unless a
reason is given:

- Spec kept in §17: per-agent `capabilities` and `policies` (CFG-01, CAP-01, readings for the owner).
- `baseUrl`, Message Batches (CLD-11) and the condition roots `checks.<name>`, `outcome` and `stopReason`: S21 records
  why. The `task` root: S18 records why.
- `operations.secrets.source`: `sof` reads environment variables. `operations.events.store` and `storeModelText`:
  replaced by `storage.unstoredEvents`. `operations.telemetry.traces`, `metrics` and `logContent`: telemetry is always
  produced, the host picks exporters, and content is never logged.
- `capabilities.workspace.type`, `root` and `baseline`: the workspace is the git repository's top folder and its checked-out
  branch.
- `set:` and `builtin:` entries in tool sets: a tool set lists names in `tools`.
- The built-in gates `requires-record`, `requires-check-passed`, `requires-task-status` (S18 records why), `route-to` and
  `rate-limit`; gates and knowledge sources have no `settings`. Edit safety is the workspace's own, not a gate.
- File includes (`{ "file": … }`), size values, a provider `type` other than its name, and loading extension assemblies
  from configuration (hosts register extensions in code).

Renamed from earlier drafts: `run.entry` is `sof run --agent`, `budget.agent` is `budget.total`, and `budget.task` is
`capabilities.taskBoard.budget`.

---

## 1. Principles

1. **One model, two forms.** Configuration files and code produce the same Options objects (CFG-02).
2. **Everything is named.** Providers, models, tools, tool sets, gates, checks, knowledge sources, agents and tool
   servers are maps keyed by name. Other settings refer to them by name.
3. **Defaults everywhere, except invariants.** Every setting has a default in code (CFG-16). Invariants (INV-01…10) have
   no setting that could switch them off; an attempt to weaken one is a validation error.
4. **No logic.** Conditions use the fixed condition language (§6); anything else that needs logic is an extension
   referred to by id (CFG-10).
5. **Formatting never changes behaviour.** Key order, whitespace and comments do not change the stable prefix, so
   reformatting a file never breaks the provider's cache.

---

## 2. Files and format

| Item | Rule |
|---|---|
| Format | JSON. Comments (`//`, `/* */`) and trailing commas are allowed. |
| Main file | `sof.json` in the project's top folder (the git repository's). |
| Environment file | `sof.<environment>.json`, merged on top of the main file when that environment is selected with `--environment`, or else with the `SOF_ENVIRONMENT` variable. |
| Schema | `"$schema"` may point to the published schema, for editor completion and validation. |
| Format version | `"formatVersion": 1`. Other versions are rejected. |

### Value forms

| Form | Meaning | Example |
|---|---|---|
| `"name"` | A named item of the kind the setting expects | `"model": "strong"` |
| `{ "secret": "NAME" }` | A secret, read from the secret source when used (CFG-09) | `"apiKey": { "secret": "ANTHROPIC_API_KEY" }` |
| `"builtin:<id>"` | A tool or gate shipped with the core | `"builtin:record.propose_fact"` |
| `"extension:<id>"` | A component the host registers (§11) | `"extension:Acme.CreateIssue"` |
| `"mcp:<server>/<tool>"` | A tool of an external tool server | `"mcp:github/create_pull_request"` |
| `"knowledge:<name>"` | A tool that searches a knowledge source (CTX-04) | `"knowledge:handbook"` |
| `"provider:<tool>"` | A tool the model provider runs itself (TOOL-13) | `"provider:web_search"` |
| `"preset:<id>"` | A preset shipped with the core | `"preset:coding-team"` |
| Durations | A .NET time span: `"hh:mm:ss"`, or `"d.hh:mm:ss"` with days | `"00:30:00"`, `"1.00:00:00"` |
| Money | USD | `25` |

A secret is never accepted as a plain string where `{ "secret": … }` is expected. The core does not scan configuration
for secret values. Names of named items cannot contain `.`, `[` or `]`, which setting paths use.

---

## 3. Top-level structure

```jsonc
{
  "$schema": "https://…/officina.schema.json",
  "formatVersion": 1,
  "extends": ["preset:coding-team"],   // presets and other files this one builds on       §13

  "project":      { },   // project identity and values for placeholders                   §4
  "providers":    { },   // model providers                                                §5.1
  "models":       { },   // model profiles                                                 §5.2
  "toolServers":  { },   // external tool servers (MCP)                                    §5.3
  "tools":        { },   // tools                                                          §5.4
  "toolSets":     { },   // named groups of tools                                          §5.5
  "gates":        { },   // named gates                                                    §5.6
  "checks":       { },   // named checks                                                   §5.7
  "knowledge":    { },   // knowledge sources                                              §5.8
  "agents":       { },   // agent definitions (roles, in a team)                           §7
  "run":          { },   // run defaults: budget, permission mode, timeouts                §8
  "capabilities": { },   // optional capabilities                                          §9
  "policies":     { },   // permission rules, global gates, masking, rate limits           §10
  "storage":      { },   // unstored events and retention                                  §12
  "operations":   { }    // telemetry, and where the local storage is                     §12
}
```

Every section is optional. The smallest valid configuration (CFG-03) is one agent with instructions:

```json
{ "agents": { "extractor": { "instructions": "Extract the invoice number, date and total from the document." } } }
```

It works because the defaults include a `claude` provider that reads the secret `ANTHROPIC_API_KEY`, and a `default`
model profile (`claude-opus-5-5`) that agents use when they name none.

---

## 4. Project and placeholders

```jsonc
"project": {
  "name": "invoice-api",
  "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" }   // free-form strings
}
```

Instructions, operating facts and checks' commands may contain placeholders written `{{namespace.name}}` (CFG-14).

| Namespace | Where | Names |
|---|---|---|
| `project.*` | Anywhere | `name`, `values.<name>` |
| `agent.*` | Anywhere | `name`, `description` |
| `caller.*` | Operating facts only | `id`, `tenant`, `attributes.<name>` |
| `work.*` | Operating facts only | `id`, `task.id`, `task.title`, `task.status` |
| `now` | Operating facts only | `{{now}}`, `{{now:date}}` |

- A `caller`, `work` or `now` placeholder in instructions is a validation error, as instructions are part of the stable
  prefix (CTX-02).
- A placeholder that cannot be filled is an error, never an empty string. Placeholders are filled once: a value that
  itself contains `{{…}}` is not filled again. Text such as `{{ example }}`, which is not a dotted name, stays as it is.
- There is no secret namespace: `{{secret.…}}`, `{{secrets.…}}` and `{{env.…}}` are rejected (INV-06).

---

## 5. Shared definitions

### 5.1 Providers

```jsonc
"providers": {
  "claude": {
    "apiKey": { "secret": "ANTHROPIC_API_KEY" },
    "retry": { "maxAttempts": 5, "initialDelay": "00:00:01", "maxDelay": "00:01:00" },   // REL-01, MDL-05
    "maxConcurrentCalls": 4,                           // MDL-08, CLD-10: calls in flight, all agents together
    "timeout": "00:10:00",                             // longest silence in a call before it is retried
    "features": {                                      // CLD-06: each off until switched on
      "structuredOutput": true, "clearToolResults": true, "taskBudget": 200000, "refusalFallback": true
    },
    "prices": {                                        // MDL-09: per million tokens; overrides the shipped table
      "claude-opus-5-5": { "input": 4.00, "output": 20.00, "cacheRead": 0.20, "cacheWrite5m": 5.00, "cacheWrite1h": 8.00 }
    }
  }
}
```

- The Claude provider ships prices for current models. Every model a profile uses needs a price, since cost budgets
  always exist (INV-07).
- Features are named by what they do, never by beta header. A feature switched on for a model that does not have it is a
  configuration error (MDL-06). Mid-conversation and turn-scoped system messages are used wherever the model takes them,
  with no setting. History shortening by the provider (`context.history.shortening: provider`) is Claude's compaction,
  which every current model but Haiku has (HIST-01).
- `structuredOutput` sends `output.schema` as Claude's `output_config.format`, so Claude's reply matches it, and the
  schema is no longer told in the instructions. Claude takes an object schema only with `additionalProperties: false`,
  which the provider adds where it is unset. It refuses some keywords, such as `minLength`, `minimum` or
  `additionalProperties: true`: such a call fails as an invalid request, so leave the feature off for those schemas.
- Rate limits are shared across the process (MDL-08, CLD-10). Calls over `maxConcurrentCalls` wait, the team lead's
  first. A wait the provider asks for (`Retry-After`) holds back every agent. `retry` covers transient and rate-limited
  failures: each wait doubles up to `maxDelay`, or is the provider's if longer; `maxAttempts` counts the first call.
  Other failures are not retried.

### 5.2 Model profiles

```jsonc
"models": {
  "strong": {
    "provider": "claude",
    "model": "claude-opus-5-5",
    "effort": "high",                // provider-declared: low | medium | high | xhigh | max
    "maxOutputTokens": 64000,
    "toolChoice": "auto",            // auto | none
    "settings": { "thinking": "{ \"type\": \"adaptive\", \"display\": \"summarized\" }" },   // other provider fields, as text
    "fallbacks": ["strong-backup"]   // MDL-04, in order
  },
  "strong-backup": { "model": "claude-opus-5", "effort": "high" },
  "cheap":         { "model": "claude-haiku-4-5" }
}
```

- `settings` are sent to the provider as top-level request fields, as JSON where the text parses (DESIGN.md §9).
- A fallback must support the tools of every slot that uses its primary (MDL-04). It serves a call when the primary is
  still unavailable after its retries (each fallback gets its own), and the next call tries the primary again. A switch is
  a `modelFallback` event and counts in the `officina.fallbacks` metric. A fallback's own fallbacks are not used.

### 5.3 Tool servers (MCP)

```jsonc
"toolServers": {
  "github":  { "transport": "stdio", "command": "github-mcp-server", "args": ["stdio"],
               "env": { "GITHUB_TOKEN": { "secret": "GITHUB_TOKEN" } } },
  "tracker": { "transport": "http", "url": "https://tracker.example.com/mcp",
               "headers": { "Authorization": { "secret": "TRACKER_AUTH" } } }
}
```

Tool lists are read when the host connects, before the tools are validated. Tools are offered sorted by name, so the
stable prefix does not depend on the server's order. A list that changes during a run does not change running
conversations (CTX-10).

### 5.4 Tools

Per-tool settings (TOOL-04). A tool's implementation declares its kind, input schema and parallel safety; configuration
can narrow them but never loosen them: a tool that declares itself `write` stays `write`.

```jsonc
"tools": {
  "read_file":   { "source": "extension:workspace.read_file" },
  "run_command": { "source": "extension:sandbox.run", "gates": ["commands"], "timeout": "00:15:00", "maxResultLength": 16000 },
  "create_issue": {
    "source": "extension:Acme.CreateIssue",
    "irreversible": true,                 // TOOL-10
    "permissions": ["issues:write"],
    "gates": ["issue-dedupe"],            // INV-04
    "receivesMaskedValues": true          // ING-06
  },
  "fetch_page":  { "source": "mcp:web/fetch", "kind": "read", "untrusted": true, "maskResults": true },   // SEC-04, ING-02
  "search_handbook": { "source": "knowledge:handbook" },
  "web_search":  {
    "source": "provider:web_search",
    "reason": "The lead researches unfamiliar libraries.",     // required (TOOL-13)
    "limits": { "maxUses": 5, "allowedDomains": ["learn.microsoft.com", "github.com"] }
  },
  "finish":      { "source": "extension:Acme.SubmitReport" }   // a tool stopWhen.finishTool can name
}
```

- A tool server's tools are writes unless configured as reads; MCP annotations are hints only.
- `approval` is `always` for irreversible tools and `never` otherwise. For approval by rule, give the tool a
  `builtin:require-approval` gate with a `when` (§5.6).
- A trimmed result is kept in full as an artifact, which a `builtin:artifact.page` tool reads (TOOL-09).

Built-in tools (`builtin:<id>`), each offered only when its capability is on (CAP-02):

| Tools | Capability | What for |
|---|---|---|
| `record.propose_fact`, `propose_finding`, `propose_decision`, `cite` | none | Propose run record changes (REC-02) |
| `artifact.page` | none | Read an artifact, such as a trimmed result (TOOL-09) |
| `tasks.create`, `update`, `claim`, `submit_for_review`, `review` | `taskBoard` | Work on the task board (TASK) |
| `team.message`, `team.start_helper` | `team` | Message another agent of the run by its id; start a helper (TEAM-07) |
| `memory.propose_change`, `memory.review` | `projectMemory` | Propose a change; the lead approves or rejects (MEM-03). Both are write tools |
| `human.ask_owner` | `humanInteraction` | Ask the owner a question (HITL-06) |
| `human.request_handoff` | none | Hand the work to a human, with the model's reason (EGR-04) |

A team agent's own handoff goes back to its lead, so there is no `team.handoff`.

`sof` also registers, as `extension:` ids: `workspace.read_file`, `search`, `edit_file`, `write_file`, `delete_file` and
`move_file` when the workspace is on (`read_file` reads a range of lines too), and `sandbox.run`, `start_process`,
`read_process_output` and `stop_process` when the sandbox is on.

### 5.5 Tool sets

```jsonc
"toolSets": {
  "files-read": ["read_file", "search"],
  "shell":      ["run_command", "start_process", "read_process_output", "stop_process"]
}
```

A tool set lists tools by their name in `tools`. An agent's `tools` lists tool sets. Tools are offered sorted by name
(CTX-03).

### 5.6 Gates

```jsonc
"gates": {
  "issue-dedupe":  { "use": "extension:Acme.IssueDedupeGate" },
  "main-branch":   { "use": "builtin:require-approval", "when": { "field": "args.branch", "in": ["main", "master"] } },
  "no-force":      { "use": "builtin:deny", "when": { "field": "args.force", "is": "true" } },
  "external-send": { "use": "builtin:untrusted-content-approval" }
}
```

- `builtin:require-approval` and `builtin:deny` act when `when` holds (always, when unset).
- `builtin:untrusted-content-approval` asks a human when `when` holds and the agent has read untrusted content (SEC-04).
- `sof` registers `extension:sandbox.commandRules`, the sandbox's command rules, when the sandbox is on.
- Edit safety (WS-07) is the workspace's own, not a gate.

### 5.7 Checks

```jsonc
"checks": {
  "build": { "command": "dotnet build" },               // WS-02, TASK-05: in the sandbox, in the working copy checked
  "tests": { "command": "dotnet test", "timeout": "00:40:00" },
  "cites": { "use": "extension:Acme.CitationsCheck" }   // the application's check, on output (OUT-03)
}
```

A check returns passed or failed, with findings. A command check needs the sandbox and checks a working copy: a task's
when it is submitted, or the change applied to the baseline before it is integrated (`capabilities.workspace.baselineChecks`).
It passes when the command exits with 0, gets no secrets, and fails after its `timeout` (20 minutes by default). Its
findings are untrusted content (SEC-04). A review is a task's (`tasks.review`), by an agent other than the author (TASK-06).

### 5.8 Knowledge sources

```jsonc
"knowledge": {
  "handbook": { "use": "extension:Acme.HandbookIndex" },
  "tickets":  { "use": "extension:Acme.TicketIndex", "mask": true }   // ING-02: mask personal data in passages
}
```

An agent searches a source before each turn when `context.retrieval.beforeTurn` lists it (§7.3), and when it chooses,
through a `knowledge:<name>` tool. A search returns at most 8 passages. Sources need `capabilities.knowledge`.

---

## 6. The condition language (CFG-13)

Conditions appear in workflow `next` rules, permission rules and gates. They read structured values only: a step's
validated structured output (`output`) and a tool's arguments (`args`).

```jsonc
{ "field": "output.severity", "is": "high" }
{ "field": "output.category", "in": ["bug", "regression"] }
{ "field": "output.score", "gte": 0.8 }               // gt | gte | lt | lte, combinable into a range
{ "field": "output.ticket", "exists": true }
{ "all": [ <condition>, <condition> ] }               // and
{ "any": [ <condition>, <condition> ] }               // or
{ "not": <condition> }
```

| Root | Refers to | Used in |
|---|---|---|
| `output` | The step's structured output | Workflow `next`, router `on`, fan-out `on` |
| `args` | The tool's arguments | Permission rules and gates |

- Paths are dotted names with optional `[n]` indexes. There are no variables, functions or arithmetic.
- Validation checks every path against the schema of the value it reads, and rejects a comparison that can never hold
  (a value of the wrong type, or one outside the field's `enum`).
- `is` and `in` compare numbers by value (`1` is `1.0`), and `"true"` or `"false"` with booleans. `gt`, `gte`, `lt` and
  `lte` compare numbers only. `exists: true` holds when the field is present and not `null`. A missing field makes every
  other test false.
- The roots `checks.<name>`, `outcome`, `stopReason` and `task` are not built; use `onOutcome` for step outcomes.

---

## 7. Agent definitions

### 7.1 Shape

```jsonc
"agents": {
  "developer": {
    "extends": "coder",                        // CFG-05: another definition to build on
    "description": "Implements one task in its own working copy.",
    "instructions": "You are a developer on the team …",
    "model": "strong",                         // a profile in models
    "tools": ["files-read", "shell"],          // tool sets
    "toolDescriptionsOnDemand": false,         // TOOL-12: offer names only; describe_tool gives the rest
    "maxParallelToolCalls": 4,                 // LOOP-08
    "pattern": { "type": "toolLoop" },         // §7.2
    "context": { },                            // §7.3
    "output": { },                             // §7.4
    "budget": { },                             // §7.5
    "permissions": ["workspace:write"],        // narrows the caller's (INV-02)
    "helpers": ["researcher"],                 // TEAM-07: agents it may start with team.start_helper; none by default
    "triggers": ["longRunning"]                // TRG-01: unset accepts every way work arrives
  }
}
```

**Model slots (MDL-03, TOOL-03).** An agent is one model slot: its `model` and `tools`. A pattern step that needs another
model or other tools names another agent defined with them. The tools offered depend only on the slot and the enabled
capabilities, never on the caller. An agent uses every capability that is on.

### 7.2 Patterns (PAT-01)

A step is a turn of another agent (`"agent": "reviewer"`), that agent's own pattern when it has one, a nested pattern
(`"pattern": {…}`, PAT-02), or, with neither, a turn of the agent the pattern belongs to. Data moves only through
declared inputs and outputs (PAT-04). Branches, routes and votes read only a turn's structured output (PAT-03). Each step
draws on the pattern's budget, which is the agent's turn budget (PAT-06). Runnable examples of each pattern are in
[`samples/`](../samples) (TEST-06).

```jsonc
{ "type": "singleCall" }            // a turn with no tools

{ "type": "toolLoop" }              // the default; ends by the agent's stop conditions (§7.5)

{ "type": "workflow",
  "steps": [
    { "id": "triage", "agent": "triager" },
    { "id": "fix", "agent": "developer", "input": ["input", "triage"],
      "onOutcome": { "failed": "retry:1", "handedOff": "handoff" } }
  ],
  "next": [ { "from": "triage", "when": { "field": "output.kind", "is": "question" }, "goto": "end" } ] }

{ "type": "router",
  "classify": { "agent": "classifier" },   // unset: a turn of this agent; its output must be structured
  "on": "output.route",
  "routes": { "bug": { "agent": "developer" }, "docs": { "agent": "writer" } },
  "otherwise": "bug" }                     // unset: hand off as no route for the value, never guess

{ "type": "fanOut",
  "over": "input.files",            // or several "branches" on the same input
  "branches": [{ "agent": "reviewer" }],
  "maxParallel": 4,
  "combine": "all" }                // all | firstSuccess | majority (with "on") | step (with "combiner")

{ "type": "evaluateAndRevise", "generate": { "agent": "developer" }, "checks": ["build", "tests"], "maxRevisions": 3 }

{ "type": "planAndExecute",         // the planner's output has a "steps" list; the executor does each item in turn
  "planner": { "agent": "planner" }, "executor": { "agent": "developer" }, "maxReplans": 2 }

{ "type": "team",                   // an agent's own pattern, never a step; needs capabilities.team
  "lead": "lead",                   // plans, decides on failed tasks, reports
  "roles": { "developer": { "max": 3 }, "reviewer": { "max": 1 } },   // developer[1]…[3], reviewer[1]; no history
  "maxParallel": 4 }                // TEAM-01, TEAM-03: the lead included

{ "type": "extension:Acme.CanaryPattern" }     // PAT-07; reads the settings above that it needs
```

In `planAndExecute`, the planner's input starts with a note that it only plans, in short steps a worker carries out one
at a time. Each item's input holds the work, the plan, every earlier item's output, each labelled with its source, and
then the item. A long plan of long outputs makes long inputs, so ask the planner for few steps, and the executor for
short replies.

A workflow step's `input` lists `input` (the workflow's input) or earlier steps' ids; several are each labelled with
their source, and a step skipped by a `goto` gives empty input. `onOutcome` maps `completed`, `handedOff` and `failed` to
`continue`, `retry:<n>`, `goto:<step>` or `handoff`, which ends the workflow with the step's result (PAT-08). The default
is `continue` on `completed` and `handoff` otherwise. A cancelled step ends the run.

### 7.3 Context (CTX)

```jsonc
"context": {
  "record": ["fact", "finding", "decision"],  // REC-06: the record entries the agent sees; unset is this list
  "recordScope": "all",                       // all | task
  "currentTask": true,                        // the status and acceptance criteria of the agent's task
  "history": { "strategy": "shortened", "shortening": "provider", "lastTurns": 10 },   // CTX-06, HIST-01
  "retrieval": { "beforeTurn": ["handbook"], "handOffWhenNotCovered": true },        // CTX-04, CTX-05
  "operatingFacts": ["Caller: {{caller.id}}", "Today: {{now:date}}"],                  // CTX-09; volatile
  "historyCacheLifetime": "00:05:00"          // CTX-11: at most 1 hour; the prefix is always cached for 1 hour
}
```

History strategies are `none` (the default), `full`, `shortened` and `lastTurns`; all but `none` need the conversation
store. `shortened` is shortened once when the model reports the input too long, by `provider` (the default; the provider
must support it) or `extension:<id>`. `full` is never shortened. `lastTurns` never edits history already sent: each
request starts a new conversation with the last turns (CTX-10).

### 7.4 Output (OUT)

```jsonc
"output": {
  "format": "structured",                            // text | structured
  "schema": "{ \"type\": \"object\", \"required\": [\"verdict\"] }",   // JSON Schema, as JSON text
  "attempts": 2,                                     // OUT-02
  "checks": ["cites"],                               // OUT-03, in order
  "onCheckFailure": "handoff",                       // handoff | revise: the findings go back to the model, within attempts
  "citations": "resolve"                             // OUT-04: off | resolve | required
}
```

The output is checked against `schema` after every reply; output that does not match goes back to the model with the
errors and the schema, within `attempts`, and is then handed off (OUT-02). Unless the provider constrains output to the
schema itself (`providers.<name>.features.structuredOutput`, on every model the agent may use), the model is told the
schema after its instructions, so it is part of the cached prefix. Describe the shape briefly in the instructions all
the same, such as `{"steps": ["…"]}`.

No setting accepts work while a configured check fails (INV-09).

### 7.5 Budgets, stop conditions and progress (LOOP-05…07, RUN-05)

```jsonc
"budget": {
  "turn":  { "iterations": 50, "toolCalls": 200, "tokens": 3000000, "cost": 5, "time": "00:45:00" },
  "total": { "cost": 15 }                       // all the agent's turns in a run; also tokens and toolCalls; unset by default
},
"stall": { "iterationsWithoutProgress": 3 },
"stopWhen": { "finished": true, "finishTool": null, "checksPass": false, "maxIterations": null },   // the first that holds completes the turn
"handOffOnPolicyGap": true                                                                         // LOOP-11
```

Every level has a limit, and none can be `null` or unlimited (INV-07). The levels are turn, pattern, agent (`total`),
task (`capabilities.taskBoard.budget`) and run (`run.budget`); DESIGN.md §8 says how they draw on each other.

---

## 8. Run defaults

```jsonc
"run": {
  "budget": { "cost": 25, "time": "08:00:00" },   // RUN-05; also "tokens" and "toolCalls", unset by default
  "permissionMode": "ask",                        // HITL-01: ask | auto | readOnly   (live)
  "approvalTimeout": "00:30:00",                  // HITL-02: then deny
  "cancelWithin": "00:00:10"                      // RUN-06
}
```

`sof run --agent <name>` picks the agent a run starts with; it may be left out when there is only one. A chat session
(plain `sof`, or `sof chat`) runs each message as a run of the `conversation` trigger, with the conversation store on and
`full` history for the agent it chats with (a team's lead), unless the configuration sets that agent's
`context.history.strategy`; it refuses to run when the configuration sets `capabilities.conversationStore.enabled` to
`false`, or the agent's `triggers` leave out `conversation`. `sof resume` runs a chat message's run the same way.

### Live settings (CFG-08)

Only these change during a run, through the host's run control (the `sof run` console), never by editing files:

| What | How in `sof` |
|---|---|
| `run.permissionMode` | `mode <name>`. Changes gate decisions only; the tools offered never change (TOOL-03) |
| Going past the run budget | The `runBudgetExceeded` sign-off: approved, the run goes on with another budget of the same size |
| Owner messages | `tell <agent> <text>`, delivered at the agent's next iteration (HITL-03) |
| Approvals, answers and sign-offs | `approve`, `deny`, `change`, `answer` (HITL-02, HITL-04) |
| Pausing, resuming and cancelling | `pause`, `resume`, `cancel`; Ctrl+C also cancels the run cleanly (RUN-06) |

No agent can change any setting (INV-10). Built-in tools cannot, extension tools get no access to configuration, and the
configuration files are read-only to agents' file tools and commands. A command could still create a new one, such as
`sof.<environment>.json`, so integration refuses any change that adds, changes or removes a protected path.

---

## 9. Capabilities

All are off by default (CAP-01). Each is an object with `enabled` plus its own settings; the settings of one that is off
are not checked.

```jsonc
"capabilities": {
  "conversationStore": { "enabled": true },                                       // CAP-05
  "knowledge":         { "enabled": true },                                       // §5.8
  "humanInteraction":  { "enabled": true, "signOffs": ["planApproval", "runBudgetExceeded", "irreversibleAction"] },   // HITL-04
  "checkpoints":       { "enabled": true, "at": ["turn", "integration"] },        // RUN-03: turn | step | integration
  "team":              { "enabled": true, "helperDepth": 2, "helperCount": 4 },   // TEAM-07
  "taskBoard":         { "enabled": true, "maxAttempts": 3,                       // TASK-09
                         "budget": { "cost": 8, "tokens": 20000000, "toolCalls": 500, "time": "02:00:00" } },   // RUN-05
  "workspace": {
    "enabled": true,
    "baselineChecks": ["build", "tests"],                                         // WS-02
    "protectedPaths": [{ "path": "secrets/**", "access": "hidden" }],             // WS-05: added to the fixed ones
    "keepWorkingCopies": false                                                    // WS-08
  },
  "sandbox": {
    "enabled": true,
    "allowedHosts": ["api.nuget.org", "*.nuget.org"],     // SBX-01; empty means no network
    "toolchains": ["/home/dev/.dotnet"],                  // read-only, and on the path
    "commandRules": [                                     // SBX-02; first match wins, unmatched is asked about
      { "match": "dotnet test",   "action": "allow" },
      { "match": "dotnet test *", "action": "allow" },    // not dotnet *, which allows dotnet run and every tool
      { "match": "git push*",     "action": "deny" }
    ],
    "secrets": { "developer": ["NUGET_TOKEN"] }           // SBX-05, by agent
  },
  "projectMemory": { "enabled": true, "scope": "project", "maxTokens": 20000, "approveBy": "lead" }   // MEM
}
```

- **Sign-offs:** unset means `runBudgetExceeded` and `irreversibleAction`. Without `runBudgetExceeded`, an exhausted run
  budget ends the turn in a handoff.
- **Checkpoints:** one is also taken when a run starts, and on demand. `at` unset means `turn`.
- **Protected paths:** `.git`, `**/.env*` and `.sof/**` are always hidden, and `sof.json`, `sof.*.json` and the files
  they extend are read-only. Use `dir/**` for a folder's contents; `dir` alone matches only the folder itself.
- **Sandbox limits** are constants: 2 CPUs, 4 GiB of memory, 1024 processes and 10 Mi characters of output. A command's
  time limit is its tool's `timeout`. An allow rule for an interpreter, such as `sh*` or `bash*`, allows every command.

Dependencies checked at validation (CAP-03):

| Needs | Of |
|---|---|
| `taskBoard` | `team`; `tasks.*` tools |
| `workspace` | `sandbox` |
| `conversationStore` | `checkpoints`; a `history.strategy` other than `none` |
| `team` | `team.*` tools; an agent's `helpers`; a `team` pattern |
| `projectMemory` | `memory.*` tools |
| `humanInteraction` | `human.ask_owner`; `projectMemory.approveBy: owner` |
| `sandbox` | `checks.<name>.command` |
| `knowledge` | any `knowledge` source |

`sof`'s own extensions need their capabilities too: the `workspace.*` tools need `workspace`, and the `sandbox.*` tools and
the `sandbox.commandRules` gate need `sandbox`.

---

## 10. Policies and admission

```jsonc
"policies": {
  "permissionRules": [                              // TOOL-05 step 2, first match wins
    { "tool": "run_command", "when": { "field": "args.command", "is": "git push" }, "action": "deny", "reason": "Pushing is the owner's job." },
    { "tool": "delete_file", "action": "ask" },
    { "tool": "create_issue", "action": "route", "to": "lead" }
  ],
  "gates": ["main-branch"],                         // gates for all tools (TOOL-05 step 3)
  "masking": {                                      // ING-02, ING-06
    "enabled": true,
    "patterns": { "email": "…", "employeeId": "EMP-[0-9]{6}" }   // replace the built-in email, phone and card patterns
  },
  "rateLimits": {                                   // ING-03
    "perOwner":  { "permits": 10, "window": "01:00:00" },
    "perTenant": { "permits": 200, "window": "1.00:00:00" },
    "perRun":    { "permits": 5, "window": "1.00:00:00" }
  },
  "anonymousPermissions": []                        // ING-05
}
```

- Work from outside passes admission before it reaches an agent (ING-01): masking, then the agent's `triggers`, then the
  owner's, tenant's and run's rate limits; the first rejection wins. A rejection is a result with its reason (ING-04).
- Masking applies to the work and to messages sent to agents; tools and knowledge sources opt in with `maskResults` and
  `mask`. A token such as `[email-1]` stands for the same value throughout the run.
- The per-run rate limit counts a run's first work item and each resume, in memory, so it applies within one process.
- Permission rule actions are `allow`, `deny` (the default), `ask` and `route` (to the agent in `to`). A rule's `when`
  reads the tool's arguments only. A call no rule matches goes on to the gates. A caller must also hold every permission
  the tool lists, and an agent's `permissions` narrow the caller's. The caller's identity comes from the host (INV-03).

---

## 11. Extensions

An extension is a .NET type that implements one extension interface (DESIGN.md §4). The host registers it in code, by
id, when it creates the `AgentRunner`; configuration refers to it as `extension:<id>`. An id the host has not
registered, or one of the wrong kind for the setting, is a configuration error (CFG-06). `sof` registers only the
workspace and sandbox tools, the command rules gate and the command checks.

---

## 12. Storage and operations

```jsonc
"storage": {
  "unstoredEvents": ["textGenerated"],              // EVT-05: published live but not stored; unset is this list
  "retention": {                                    // PRIV-01; unset keeps the data until it is deleted
    "runs": "90.00:00:00", "events": "30.00:00:00", "conversations": "90.00:00:00", "runRecords": "365.00:00:00",
    "taskBoards": "365.00:00:00", "artifacts": "90.00:00:00", "audit": "365.00:00:00"
  }
},
"operations": {
  "telemetry": { "cacheHitWarning": 0.7 },          // COST-01
  "storage": {                                      // STO-01: the default local storage, for sof
    "path": ".sof/sof.db"                           // the SQLite database; the folder `artifacts` beside it
  }
}
```

- `sof` keeps its storage in `.sof/sof.db` (SQLite) next to `sof.json`, with each artifact, such as the full text of a
  trimmed tool result, as a file in `.sof/artifacts`, and the working copies in `.sof/worktrees`. Add `.sof/` to
  `.gitignore`; the workspace hides it from agents.
- `operations.storage.path` moves the storage, the artifacts' folder with it: that folder is always `artifacts` beside
  the database, and the database's alone. A relative path is relative to the project directory and must stay in `.sof/`;
  an absolute path must lead into the project's `.sof/` or out of the project. So the storage is never where agents can
  see it, and leaving the project takes an absolute path, which says the owner meant it. `config validate` and every
  command refuse any other path. A host that supplies its own `IStorage` in code ignores the setting, so there is no
  `type`: the in-memory storage is the test kit's, and another storage is registered in code, not named in configuration.
- `sof` reads secrets from environment variables.
- Traces, metrics and logs are always produced, through `ActivitySource`, `Meter` and `EventSource` (DESIGN.md §2); the
  host chooses the exporters. Conversation content is not logged.

---

## 13. Layering and merging (CFG-04)

The CLI loads configuration with Microsoft.Extensions.Configuration. Layers, lowest to highest:

```
code defaults < what sof.json extends < sof.json < sof.<environment>.json < environment variables < command-line options
```

- **Environment variables** look like `SOF__agents__developer__budget__turn__cost=8`.
- **Command-line options** are the CLI's own, such as `--budget 40` and `--permission-mode auto`.
- **Setting names match ignoring case.** Unknown settings are ignored.
- **`extends` in a file** lists presets (`preset:<id>`, §15) and other files, relative to it, each a layer below the
  file, lowest first, each once. A cycle or a missing preset is a Merge error (§14). An extended file inside the
  workspace is read-only to agents, like `sof.json`.
- **`extends` in an agent** names another agent it builds on: it inherits what it does not set, with the rules below,
  after the files are merged. `sof config show --origin` names an inherited setting's source as "inherited by agents.X
  through extends". A cycle or a missing agent is a Merge error.

| Value | Rule |
|---|---|
| Object | Merged key by key |
| Named map (`agents`, `tools`, …) | Merged by name; a new name adds an entry |
| List | Replaced whole by the higher layer's list. Environment variables and options merge a list by position, so set it in one layer |
| `null` | Unsets a setting that may be unset. It never removes a budget |

`sof config show` lists every effective setting, defaults included. With `--origin`, each also shows its source: the
file, variable or option that set it, or `code default, core 0.1.0`.

---

## 14. Validation (CFG-06)

Validation runs in full before anything runs and reports every error it finds at once. Each error names the setting,
the problem and the fix, and where it was written. A value that cannot be converted to its type is reported one at a
time, with the binder's message. For example:

```
sof.json:4:16: agents.developer.tools: tool set "shel" does not exist. Add it to toolSets, or use one of: files-read, shell.
tools.create_issue: write tool has no gate of its own. Add "gates": [...] or "gateExemption": "<reason>".
agents.lead.instructions: placeholder {{caller.id}} is not allowed in instructions. Instructions are the same for every call, so they cannot use caller, work or time values.
```

| # | Phase | Rejects |
|---|---|---|
| 1 | Parse | Invalid JSON, unknown `formatVersion` |
| 2 | Shape | Wrong types, values out of range, missing required settings |
| 3 | Merge | Cycles in `extends`, missing presets and agents to build on |
| 4 | References | Missing models, tools, tool sets, gates, checks, knowledge sources, agents, tool servers and extensions; placeholders that cannot be filled |
| 5 | Capabilities | Capabilities used but off, unmet dependencies (§9) |
| 6 | Provider | Tools, features or shortening the model does not support (MDL-06), fallbacks that cannot serve their slots (MDL-04), models without prices |
| 7 | Tools | Duplicate names, invalid input schemas, write tools without gates or exemptions (TOOL-02) |
| 8 | Conditions | Paths that do not exist in the schema they read, comparisons that can never hold |
| 9 | Prefix | Volatile placeholders in instructions (CTX-02) |
| 10 | Invariants | Attempts to weaken INV-01…10 (below) |

| Invariant | Rejected configuration |
|---|---|
| INV-01 | A condition on free-text output; a router whose classification is not structured |
| INV-02, INV-03 | There is no setting for a tool's identity; agent `permissions` only narrow the caller's |
| INV-04 | A write tool with neither gates nor an exemption with a reason |
| INV-05 | There is no setting that switches audit off; `storage.retention.audit` only limits how long entries are kept |
| INV-06 | A secret written as a plain string; a secret placeholder |
| INV-07 | A budget of zero or less, or `null` for a budget |
| INV-08 | There is no setting that places tool or document content in instructions |
| INV-09 | There is no `onCheckFailure` that accepts; a task cannot be done without its checks |
| INV-10 | Configuration files cannot be made writable to agents |

A setting rejected in an early phase is not reported again. `sof config validate` runs every phase and exits 1 on any
error. It also reports what `sof run` refuses as it starts: an `extension:` id `sof` does not register, and a provider
tool, feature or history shortening a model's provider lacks. What only a chat session refuses (see §8) it prints as a
note, as the other commands run with it. `sof config dry-run [--agent <name>] [--input <text>]
--reply <text>…` also runs an agent against a scripted model (CFG-12), with the workspace's files in memory and the
sandbox's commands answered without running them.

---

## 15. Presets

A preset is a configuration file shipped with the core, used through `extends`. It is an ordinary lower layer, so the
application's file overrides it with the same merge rules. The presets are in
[`src/Sleepyshark.Officina.Core/Presets/`](../src/Sleepyshark.Officina.Core/Presets/) (CFG-11).

**`preset:single-call-extractor`:** one agent, `extractor`, with the `singleCall` pattern and structured output, no
history and no capabilities. The application gives `agents.extractor.instructions` and `output.schema`.

**`preset:tool-using-assistant`:** one agent, `assistant`, with the `toolLoop` pattern and full history; the conversation
store and human interaction on; permission mode `ask`. The application gives `agents.assistant.instructions` and `tools`.

**`preset:coding-team`:** the `team` agent, a lead, up to three developers and a reviewer.

- **Capabilities:** team, task board, workspace, sandbox, project memory (the lead approves), checkpoints (after each
  turn and each integration), conversation store, and human interaction with sign-offs for plan approval, the run budget
  and irreversible actions (HITL-04).
- **Masking:** off, as it would corrupt source code (ING-02).
- **Command rules:** `git push` and `git remote` denied; no git command is allowed, as the sandbox hides `.git`. The application
  adds its own project's commands, repeating these, since a list replaces the preset's.
- **Checks:** `build` and `tests`, from `project.values.buildCommand` and `testCommand`, used for tasks and the baseline.
- **Permission rules** allow the file writes and commands, as writes stay in a task's working copy until its checks and
  review pass, and commands still pass the command rules.
- **Roles,** each built on the shared `coder` definition (CFG-05):

| Role | Model (effort) | Tools | Job |
|---|---|---|---|
| `lead` | `claude-opus-5-5` (high) | read files, tasks, team message, memory review, human | Plans, decides on failed tasks, re-plans. Changes no files |
| `developer` (max 3) | `claude-opus-5-5` (high) | files, shell, `tasks.submit_for_review`, team message, memory proposals, human | Does a task in its working copy until its checks pass |
| `reviewer` (max 1) | `claude-opus-5-5` (medium) | read files, `tasks.review`, team message, `human.request_handoff` | Reviews another agent's task; never the author |

The reviewer has no write tools and no commands: a command runs in the task's working copy, which TASK-06 keeps for the
task's author, and its effects could not be told apart.

An application using the coding team needs only:

```json
{
  "extends": ["preset:coding-team"],
  "project": { "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" } },
  "capabilities": { "sandbox": { "allowedHosts": ["api.nuget.org", "*.nuget.org"] } }
}
```

[`benchmark/sof.json`](../benchmark/sof.json) is such a file, with command rules for `dotnet`.

---

## 16. Requirement coverage

| Requirement | Section |
|---|---|
| CFG-01 | §7 |
| CFG-02 | §1 |
| CFG-03 | §3 |
| CFG-04, CFG-05 | §13 |
| CFG-06 | §14 |
| CFG-07 | Each run stores its resolved configuration (DESIGN.md §8) |
| CFG-08 | §8 |
| CFG-09 | §2 |
| CFG-10 | §1, §11 |
| CFG-11 | §15 |
| CFG-12 | §14 |
| CFG-13 | §6 |
| CFG-14 | §4 |
| CFG-15 | §2 |
| INV-01…10 | §14 |
| MDL-02…04, MDL-09 | §5.1, §5.2 |
| TOOL-01…04, TOOL-13 | §5.3–§5.5 |
| CTX-01, CTX-04…06, CTX-09, CTX-11 | §7.3 |
| OUT-01…04 | §7.4 |
| LOOP-05…07, RUN-05 | §7.2, §7.5, §8 |
| PAT-01…08 | §7.2 |
| CAP-01…03 | §9 |
| HITL-01, HITL-02, HITL-04 | §8, §9 |
| ING-01…06 | §10 |
| SBX-01…05, WS-02, WS-05, WS-08, MEM-04, MEM-05 | §9 |
| STO-01, PRIV-01, EVT-05, OBS-01…03 | §12 |

---

## 17. Not built in v1: spec for open requirements

Kept so the shape is known if the owner decides to build them at revision 3.

- **Per-agent capabilities and policies (CFG-01, CAP-01).** An agent's `capabilities` lists which of the enabled
  capabilities it uses, such as `["workspace", "sandbox", "taskBoard"]`; unset, it uses all. Listing one that is not
  enabled is a CAP-03 error. An agent's `policies`, such as `{ "gates": ["tests-first"] }`, adds gates for all its tools.
