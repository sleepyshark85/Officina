# Officina — Configuration Reference (draft)

Status: draft for M0 design review · 2026-09-30 · companion to `../REQUIREMENTS.md` (revision 2)

This is the full catalogue of settings. **Most applications never need it**: start with
`../CONFIGURATION.md`, which lists the few settings a new application must specify.

Every setting here has its default defined in code, in the core's Options classes (CFG-16). The
settings implemented so far are listed in the generated [settings reference](configuration-settings.md),
and the generated JSON Schema is [`officina.schema.json`](officina.schema.json) (CFG-15, DOC-01). For
settings not implemented yet, this draft is the specification they must match.

Settings are added when the slice that needs them is built. A setting no slice needs yet stays a
constant in code, and can be dropped from this draft rather than built speculatively.

---

## 1. Principles

1. **One model, two forms.** Configuration files and the programmatic builder produce the same
   configuration objects (CFG-02). Everything below applies to both.
2. **Everything is named.** Providers, models, tools, tool sets, gates, checks, knowledge sources,
   agents and tool servers are maps keyed by name. Names are what other settings refer to.
3. **Defaults everywhere, except invariants.** Every setting has a documented default (CFG-03).
   Invariants (INV-01…10) have no setting that could switch them off; an attempt to weaken one is
   a validation error, not an ignored value.
4. **No logic.** Conditions use the fixed condition language (§6); everything else that needs logic
   is an extension referred to by name (CFG-10).
5. **Behaviour-neutral formatting.** Key order, whitespace and comments never change behaviour. The
   stable prefix is rendered from the resolved configuration in a canonical order, so reformatting a
   file never breaks the provider's cache.

---

## 2. Files and format

| Item | Rule |
|---|---|
| Format | JSON. Comments (`//`, `/* */`) and trailing commas are accepted and ignored. |
| Main file | `sof.json` in the application's directory (the coding team CLI: the project root). |
| Environment file | `sof.<environment>.json`, merged on top of the main file when that environment is selected: by the host (the CLI's `--environment`), or else by the `SOF_ENVIRONMENT` variable. |
| Schema | `"$schema"` may point to the published schema, which gives editor completion and validation. |
| Format version | `"formatVersion": 1`. Unknown versions are rejected. A newer core reads the previous format version (REL-04). |
| Includes | Large values can live in their own files: `{ "file": "prompts/developer.md" }` for text, and the same form for JSON Schemas. Paths are relative to the file that contains them. |
| Encoding | UTF-8. Text loaded from files is normalised to `\n` line endings, so the stable prefix is byte-identical on Linux and Windows (COST-01). |

### Value forms

| Form | Meaning | Example |
|---|---|---|
| `"name"` | A reference to a named item of the kind the setting expects | `"model": "strong"` |
| `{ "secret": "NAME" }` | A secret resolved from the secret source at use time (CFG-09) | `"apiKey": { "secret": "ANTHROPIC_API_KEY" }` |
| `{ "file": "path" }` | File contents | `"instructions": { "file": "prompts/lead.md" }` |
| `"builtin:<id>"` | A component shipped with the core | `"builtin:workspace.edit_file"` |
| `"extension:<id>"` | A component supplied by the application (§11) | `"extension:Acme.CreateIssue"` |
| `"mcp:<server>/<tool>"` | A tool from an external tool server | `"mcp:github/create_pull_request"` |
| `"knowledge:<name>"` | A tool that searches a knowledge source (CTX-04) | `"knowledge:handbook"` |
| `"provider:<tool>"` | A provider server-side tool (TOOL-13) | `"provider:web_search"` |
| `"preset:<id>"` | A preset shipped with the core | `"preset:coding-team"` |
| Durations | A .NET time span: `"hh:mm:ss"`, or `"d.hh:mm:ss"` with days | `"00:30:00"`, `"1.00:00:00"` |
| Sizes | A number with a unit: `B`, `KiB`, `MiB`, `GiB` | `"4GiB"` |
| Money | A number in the currency of the price table (default USD) | `25` |

A secret is never accepted as a plain string where a `{ "secret": … }` is expected (CFG-09).
Configuration should not hold secret values; the core does not scan it for them.

Names of named items (agents, models, providers, …) cannot contain `.`, `[` or `]`, which setting
paths use.

---

## 3. Top-level structure

```jsonc
{
  "$schema": "https://…/officina.schema.json",
  "formatVersion": 1,
  "extends": ["preset:coding-team"],   // presets and other files this configuration builds on

  "project":      { },   // project identity and values usable in placeholders            §4
  "providers":    { },   // model providers                                              §5.1
  "models":       { },   // model profiles                                               §5.2
  "toolServers":  { },   // external tool servers (MCP)                                  §5.3
  "tools":        { },   // per-tool settings                                            §5.4
  "toolSets":     { },   // named groups of tools                                        §5.5
  "gates":        { },   // named gates                                                  §5.6
  "checks":       { },   // named output and verification checks                         §5.7
  "knowledge":    { },   // knowledge sources                                            §5.8
  "agents":       { },   // agent definitions (roles, in a team)                         §7
  "run":          { },   // run defaults: entry agent, budget, permission mode           §8
  "capabilities": { },   // optional capabilities and their settings                     §9
  "policies":     { },   // permission rules, global gates, masking, rate limits         §10
  "operations":   { },   // storage, retention, events, telemetry, secrets               §12
  "extensions":   { }    // where extension assemblies are loaded from                   §11
}
```

Every section is optional. The smallest valid configuration (CFG-03) is one agent with
instructions:

```json
{
  "agents": {
    "extractor": {
      "instructions": "Extract the invoice number, date and total from the document."
    }
  }
}
```

This works because the core defaults include a `claude` provider that reads the secret
`ANTHROPIC_API_KEY`, and a `default` model profile that agents use when they name none.

---

## 4. Project and placeholders

```jsonc
"project": {
  "name": "invoice-api",
  "values": {                          // free-form strings, usable in placeholders
    "buildCommand": "dotnet build",
    "testCommand": "dotnet test"
  }
}
```

Instructions and some settings may contain placeholders written as `{{namespace.name}}` (CFG-14).

| Namespace | Available in | Examples |
|---|---|---|
| `project.*` | Anywhere, including the stable prefix | `{{project.name}}`, `{{project.values.testCommand}}` |
| `agent.*` | Anywhere, including the stable prefix | `{{agent.name}}`, `{{agent.description}}` |
| `caller.*` | The volatile context only | `{{caller.id}}`, `{{caller.attributes.locale}}` |
| `work.*` | The volatile context only | `{{work.id}}`, `{{work.task.title}}` |
| `now` | The volatile context only | `{{now}}`, `{{now:date}}` |

A placeholder from a volatile-only namespace inside instructions, tool descriptions, policies or
memory is a validation error that names the setting and suggests `context.operatingFacts` (§7.3).
An unknown placeholder is an error, never an empty string.

- `project.*` has `name` and `values.<name>`; `agent.*` has `name` and `description`.
- Placeholders are filled once: a value that itself contains `{{…}}` is not filled again.
- Text between `{{` and `}}` that is not a dotted name, such as `{{ example }}`, is left as it is.
- There is no secret namespace: `{{secret.…}}`, `{{secrets.…}}` and `{{env.…}}` are rejected (INV-06).

---

## 5. Shared definitions

### 5.1 Providers

```jsonc
"providers": {
  "claude": {
    "type": "claude",                                  // provider implementation
    "apiKey": { "secret": "ANTHROPIC_API_KEY" },
    "baseUrl": null,                                   // optional override
    "retry": { "maxAttempts": 5, "initialDelay": "00:00:01", "maxDelay": "00:01:00" },   // REL-01, MDL-05
    "timeout": "00:10:00",
    "features": {                                      // provider features to use, when models support them
      "midConversationSystemMessages": true,           // CLD-03
      "turnScopedSystemMessages": true,                // CLD-03, CTX-10 (beta)
      "compaction": false,                             // CLD-06, HIST-01 (beta)
      "clearOldToolResults": false,                    // CLD-06, HIST-05 (beta)
      "taskBudgets": false,                            // CLD-06 (beta)
      "refusalFallback": "off",                        // CLD-06: "off" | "default" (beta)
      "batch": true                                    // CLD-11
    },
    "prices": {                                        // MDL-09; overrides the provider's shipped table
      "claude-opus-5-5": { "input": 4.00, "output": 20.00, "cacheRead": 0.20,
                           "cacheWrite5m": 5.00, "cacheWrite1h": 8.00 }
    }
  }
}
```

- The Claude provider ships a price table for current models. Prices are per million tokens.
  Configured prices replace shipped ones per model, and a model with no known price fails
  validation when any cost budget is set (MDL-09).
- Beta features are named by what they do, never by beta header. The provider maps each one to
  whatever header is current, and it fails validation when a feature is on but the selected model
  or platform does not support it (MDL-06).
- Rate limits are shared across the process automatically (MDL-08, CLD-10); there is no setting
  to switch that off.

### 5.2 Model profiles

```jsonc
"models": {
  "strong": {
    "provider": "claude",
    "model": "claude-opus-5-5",
    "effort": "high",                // provider-declared: low | medium | high | xhigh | max
    "maxOutputTokens": 64000,
    "toolChoice": "auto",            // auto | none; other modes only where the provider declares them
    "settings": {                    // any other provider-declared setting (MDL-02)
      "thinkingDisplay": "summarized"
    },
    "fallbacks": ["strong-backup"]   // MDL-04, in order
  },
  "strong-backup": { "provider": "claude", "model": "claude-opus-5", "effort": "high" },
  "fast":          { "provider": "claude", "model": "claude-sonnet-5-5", "effort": "medium" },
  "cheap":         { "provider": "claude", "model": "claude-haiku-4-5" }
}
```

- `settings` is validated against the settings the provider declares for that model. For example,
  a setting that turns off thinking on a model where thinking cannot be off is a validation error.
- A fallback must support the tools and output format of every slot that uses its primary
  (MDL-04). Switching to a fallback mid-conversation is recorded, and its cache and reasoning
  effects are reported, because provider caches and reasoning are tied to the model.

### 5.3 Tool servers (MCP)

```jsonc
"toolServers": {
  "github": {
    "transport": "stdio",                        // stdio | http (Streamable HTTP)
    "command": "github-mcp-server",
    "args": ["stdio"],
    "env": { "GITHUB_TOKEN": { "secret": "GITHUB_TOKEN" } }
  },
  "tracker": {
    "transport": "http",
    "url": "https://tracker.example.com/mcp",
    "headers": { "Authorization": { "secret": "TRACKER_AUTH" } }
  }
}
```

Tool lists from servers are read when the host connects to them, before the tools are validated.
Tools are offered sorted by name, so the stable prefix does not depend on the server's order. A server whose tool list changes during a
run does not change running conversations (CTX-10); new conversations see the new list.

### 5.4 Tools

Per-tool settings (TOOL-04). Built-in and extension tools declare defaults (kind, input format,
parallel safety); configuration can narrow them but never loosen what the tool declares. For
example, a tool that declares itself `write` cannot be configured as `read`.

```jsonc
"tools": {
  "read_file":   { "source": "builtin:workspace.read_file" },
  "search":      { "source": "builtin:workspace.search" },
  "edit_file":   { "source": "builtin:workspace.edit_file" },     // has built-in gates (WS-07)
  "run_command": {
    "source": "builtin:sandbox.run",
    "timeout": "00:15:00",
    "maxResultLength": 16000                                       // TOOL-09
  },
  "create_issue": {
    "source": "extension:Acme.CreateIssue",
    "kind": "write",
    "irreversible": true,                                          // TOOL-10
    "permissions": ["issues:write"],
    "gates": ["issue-dedupe"],                                     // INV-04
    "approval": "always",                                          // never | always
    "maxAttempts": 1,
    "receivesMaskedValues": true                                   // ING-06
  },
  "fetch_page":  { "source": "mcp:web/fetch", "kind": "read", "untrusted": true, "maskResults": true },   // SEC-04, ING-02
  "create_pr":   { "source": "mcp:github/create_pull_request", "gates": ["github-writes"] },
  "get_issue":   { "source": "mcp:github/get_issue", "kind": "read" },
  "search_handbook": { "source": "knowledge:handbook" },           // CTX-04, as a tool
  "web_search":  {
    "source": "provider:web_search",
    "reason": "Lead researches unfamiliar libraries",               // required (TOOL-13)
    "limits": { "maxUses": 5, "allowedDomains": ["learn.microsoft.com", "github.com"] }
  }
}
```

| Setting | Default | Notes |
|---|---|---|
| `source` | required | Where the tool comes from (§2 value forms). |
| `kind` | the tool's declaration; `write` for MCP tools | MCP annotations are treated as hints only. A tool server's tools are writes unless configured as reads. |
| `permissions` | `[]` | Permissions the caller must hold (TOOL-03, INV-02). |
| `gates` | `[]` | The tool's own gates, run after global gates (TOOL-05). |
| `gateExemption` | none | The reason a write tool needs no gate of its own (INV-04). |
| `approval` | `always` for irreversible tools, otherwise `never` | `always` or `never`. For approval by rule, give the tool a `builtin:require-approval` gate whose `when` is a condition over the tool's arguments. |
| `timeout` | `2m` | Per call. |
| `maxAttempts` | `1` | Retried only for `timeout` and `unavailable` errors, and never for irreversible tools. |
| `maxResultLength` | `32000` | Characters of a result that enter the conversation; the rest is cut off. The full result is kept as an artifact the agent can page through (S06). |
| `parallelSafe` | the tool's declaration; `false` for MCP tools | LOOP-08. |
| `irreversible` | `false` | Carried out at most once, with an idempotency key (TOOL-10). |
| `receivesMaskedValues` | `false` | Masked values are restored in this tool's arguments (ING-06). Its results are masked again. |
| `maskResults` | `false` | Personal data in the results is masked when masking is on (ING-02). |
| `untrusted` | `false` | The results are untrusted content; an agent that read them is marked for gates (SEC-04). |
| `description` | the tool's own | An override; it becomes part of the stable prefix. |

Built-in tool packs (TOOL-01) are only offered when their capability is on (CAP-02):

- **`workspace.*`:** `read_file`, `read_range`, `search`, `list`, `edit_file`, `write_file`,
  `delete_file`, `move_file`.
- **`sandbox.*`:** `run`, `start_process`, `read_process_output`, `stop_process`.
- **`record.*`:** `propose_fact`, `propose_finding`, `propose_decision`, `cite` (REC-02).
- **`tasks.*`:** `create`, `update`, `claim`, `submit_for_review`, `review` (TASK).
- **`team.*`:** `message`, `start_helper`, `handoff`.
- **`memory.*`:** `propose_change`.
- **`human.*`:** `ask_owner`, `request_handoff` (HITL-06, EGR-04).
- **`artifact.*`:** `page` (TOOL-09).
- **`control.*`:** `finish`, the designated finish tool (LOOP-05).

### 5.5 Tool sets

```jsonc
"toolSets": {
  "files":  ["read_file", "read_range", "search", "list", "edit_file", "write_file"],
  "shell":  ["run_command", "start_process", "read_process_output", "stop_process"],
  "record": ["builtin:record.*"],
  "review": ["read_file", "read_range", "search", "builtin:tasks.review"]
}
```

A tool set may list configured tool names, built-in tools directly (`builtin:…`), or other tool
sets (`"set:files"`). Tools are presented to the model sorted by name (CTX-03), whatever order
they are listed in.

### 5.6 Gates

```jsonc
"gates": {
  "issue-dedupe":   { "use": "extension:Acme.IssueDedupeGate" },
  "github-writes":  { "use": "builtin:require-approval", "when": { "field": "args.branch", "in": ["main", "master"] } },
  "tests-first":    { "use": "builtin:requires-check-passed", "settings": { "check": "tests" } }
}
```

The built-in gates are:
- `requires-prior-read`: an edit requires a read of the same file at the same version (WS-07).
- `requires-record`: a fact or decision must exist first (TOOL-06).
- `requires-check-passed`.
- `requires-task-status`.
- `require-approval` (with `when`).
- `deny` (with `when`).
- `route-to` (to an agent or a human).
- `rate-limit`.
- `untrusted-content-approval` (with `when`): asks a human once the agent has read untrusted content (SEC-04).

### 5.7 Checks

```jsonc
"checks": {
  "build":  { "use": "builtin:command", "settings": { "command": "{{project.values.buildCommand}}", "timeout": "00:10:00" } },
  "tests":  { "use": "builtin:command", "settings": { "command": "{{project.values.testCommand}}", "timeout": "00:20:00" } },
  "review": { "use": "builtin:agent-review", "settings": { "reviewer": "reviewer" } },
  "cites":  { "use": "builtin:citations-resolve" }
}
```

A check returns a structured result: passed or failed, with findings. `builtin:agent-review` runs
another agent whose output is a schema-validated verdict, so even a review decides by a structured
signal (INV-01). The reviewer is never the author (TASK-06).

### 5.8 Knowledge sources

```jsonc
"knowledge": {
  "handbook": { "use": "extension:Acme.HandbookIndex", "settings": { "index": "prod" } },
  "tickets":  { "use": "extension:Acme.TicketIndex", "mask": true }   // ING-02: mask personal data in passages
}
```

An agent searches a source before each turn when its `context.retrieval.beforeTurn` lists it
(§7.3), and when it chooses through a tool whose source is `knowledge:<name>` (§5.4). Both can be
used together. A search returns at most 8 passages.

---

## 6. The condition language (CFG-13)

Conditions appear in branch rules, router mappings, approval rules and gates. They read
**structured values only**: validated structured output, check results, stop reasons, tool
arguments and task fields. A condition that refers to free-text output fails validation (INV-01).

```jsonc
{ "field": "output.severity", "is": "high" }
{ "field": "output.category", "in": ["bug", "regression"] }
{ "field": "output.score", "gte": 0.8 }                 // gt | gte | lt | lte, combinable into a range
{ "field": "checks.tests.passed", "is": "true" }
{ "field": "output.ticket", "exists": true }
{ "all": [ <condition>, <condition> ] }                 // and
{ "any": [ <condition>, <condition> ] }                 // or
{ "not": <condition> }
```

| Field root | Refers to |
|---|---|
| `output` | The step's validated structured output |
| `checks.<name>` | A check result: `passed`, plus `findings` (count only) |
| `outcome` | The step outcome: `completed`, `handedOff`, `failed` or `cancelled` |
| `stopReason` | The last stop reason |
| `args` | Tool arguments (approval rules and gates only) |
| `task` | The current task's fields (gates, when the task board is on) |

Paths are dotted names with an optional `[n]` index. There are no variables, functions or
arithmetic. Validation checks every path against the schema of the value it reads, and rejects a
comparison that can never hold (a value of the wrong type, or one outside the field's `enum`).

`is` and `in` compare the field's value with the values given: numbers by value (`1` is `1.0`), and
`true` or `false` with booleans. `gt`, `gte`, `lt` and `lte`
compare numbers only. `exists: true` holds when the field is present and not `null`. A field that is
missing makes every other test false.

---

## 7. Agent definitions

### 7.1 Shape

```jsonc
"agents": {
  "developer": {
    "extends": "base-coder",                   // CFG-05: another definition to build on
    "description": "Implements one task in its own working copy.",
    "instructions": { "file": "prompts/developer.md" },
    "model": "strong",                         // default model slot
    "tools": ["files", "shell", "record"],     // default tool sets for all this agent's model slots
    "toolDescriptionsOnDemand": false,         // TOOL-12: offer names only; describe_tool gives the rest
    "pattern": { "type": "toolLoop" },         // §7.2
    "context": { },                            // §7.3
    "output": { },                             // §7.4
    "budget": { },                             // §7.5
    "permissions": ["workspace:write", "sandbox:run"],   // narrows the owner's (INV-02)
    "policies": { "gates": ["tests-first"] },            // gates for all this agent's tools
    "capabilities": ["workspace", "sandbox", "projectMemory", "taskBoard"],
    "helpers": { "allowed": false },           // TEAM-07
    "triggers": ["longRunning"]                // TRG-01: unset accepts work that arrives any way
  }
}
```

**Model slots and tools (MDL-03, TOOL-03).** The agent's `model` and `tools` are the defaults for
every model slot. A step inside a pattern that calls a model is its own slot: it may set its own
`model` and `tools`, and it inherits whatever it does not set. The tools offered in a slot depend
only on that slot's configuration and the enabled capabilities, never on the caller.

**Capabilities per agent.** `capabilities` lists which of the application's enabled capabilities
this agent uses; when it is not set, the agent uses all of them. Listing a capability the
application has not enabled is a validation error (CAP-03).

### 7.2 Patterns (PAT-01)

Each step is either an inline agent-like block (`model`, `tools`, `instructions`, `output`), a
reference to another agent definition (`"agent": "reviewer"`), or a nested pattern (`"pattern": {…}`,
PAT-02). Data moves only through declared `input` and `output` (PAT-04).

```jsonc
{ "type": "singleCall" }

{ "type": "toolLoop" }              // ends by the agent's stop conditions (§7.5)

{ "type": "workflow",
  "steps": [
    { "id": "triage",  "model": "fast", "tools": [], "output": { "format": "structured", "schema": { "file": "schemas/triage.json" } } },
    { "id": "fix",     "agent": "developer", "input": { "from": "triage.output" },
      "onOutcome": { "failed": { "retry": 1 }, "handedOff": "handoff" } }
  ],
  "next": [
    { "from": "triage", "when": { "field": "output.kind", "is": "question" }, "goto": "end" },
    { "from": "triage", "goto": "fix" }
  ] }

{ "type": "router",
  "classify": { "model": "cheap", "output": { "format": "structured", "schema": { "file": "schemas/route.json" } } },
  "on": "output.route",
  "routes": { "bug": { "agent": "developer" }, "docs": { "agent": "writer" } },
  "otherwise": "handoff" }          // PAT-03; may name a route, never guesses

{ "type": "fanOut",
  "over": "input.files",
  "branch": { "agent": "reviewer" },
  "maxParallel": 4,
  "combine": "all" }                // all | "firstSuccess" | { "majorityOn": "output.verdict" } | { "step": {…} }

{ "type": "evaluateAndRevise",
  "generate": { "agent": "developer" },
  "checks": ["build", "tests"],
  "maxRevisions": 3 }

{ "type": "planAndExecute",
  "planner":  { "model": "strong", "output": { "format": "structured", "schema": { "file": "schemas/plan.json" } } },
  "executor": { "agent": "developer" },
  "maxReplans": 2 }

{ "type": "team",
  "lead": "lead",
  "roles": { "developer": { "max": 3 }, "reviewer": { "max": 1 } },
  "maxParallel": 4 }                // TEAM-01, TEAM-03

{ "type": "extension:Acme.CanaryPattern", "settings": { } }     // PAT-07
```

`onOutcome` maps each outcome (`completed`, `handedOff`, `failed`, `cancelled`) to `"continue"`,
`{ "retry": n }`, `{ "goto": "<step>" }` or `"handoff"` (PAT-08). The default is `continue` on
`completed` and `handoff` on everything else.

### 7.3 Context (CTX)

```jsonc
"context": {
  "record": ["fact", "finding", "decision"],  // REC-06, CTX-01: the run record entries the agent sees; unset is this list
  "currentTask": true,                // CTX-01: the status and acceptance criteria of the task the agent works on
  "history": { "strategy": "shortened", "shortening": "provider", "lastTurns": null },  // CTX-06, HIST-01
  "retrieval": {                      // CTX-04, CTX-05; as a tool, see §5.8
    "beforeTurn": ["handbook"],
    "handOffWhenNotCovered": true     // policy gap when no source covers the work
  },
  "operatingFacts": [                 // CTX-09; volatile, so caller/work/now placeholders are allowed
    "Caller: {{caller.id}}",
    "Today: {{now:date}}"
  ],
  "recordScope": "all",               // REC-06: all | "task" | { "kinds": [...] }
  "historyCacheLifetime": "00:05:00"  // CTX-11: at most 1 hour; the prefix is always cached for 1 hour
}
```

History strategies are `none`, `full`, `shortened` and `lastTurns`; all but `none` need the
conversation store. `shortened` is shortened once when the model reports it too long, by
`provider` (the default; the provider must support it) or `extension:<id>`. `full` is never
shortened, which is how shortening is off. `lastTurns` never edits history already sent: each
request starts a new conversation with the last turns (CTX-10).

### 7.4 Output (OUT)

```jsonc
"output": {
  "format": "structured",                            // text | structured
  "schema": { "file": "schemas/verdict.json" },
  "attempts": 2,                                     // OUT-02
  "checks": ["cites"],                               // OUT-03, in order
  "onCheckFailure": "handoff",                       // handoff | revise (only where the pattern supports it)
  "citations": "resolve"                             // OUT-04: off | resolve | required
}
```

There is no setting that accepts work while a configured check fails (INV-09).

### 7.5 Budgets and progress (LOOP-06, LOOP-07, RUN-05)

```jsonc
"budget": {
  "turn":  { "iterations": 50, "toolCalls": 200, "tokens": 3000000, "cost": 5, "time": "00:45:00" },
  "agent": { "cost": 15 },
  "task":  { "cost": 8, "time": "02:00:00" }
},
"stall": { "iterationsWithoutProgress": 3 },
"stopWhen": { "finished": true, "finishTool": null, "maxIterations": null },  // LOOP-05; the first that holds completes the turn
"handOffOnPolicyGap": true                                                     // LOOP-11
```

Every level has a default, and none can be `null` or unlimited (INV-07). A budget can be very high,
but it always exists. The stop condition "the output passes its checks" arrives with output checks (S06).

---

## 8. Run defaults

```jsonc
"run": {
  "entry": "team",                                 // the agent definition a run starts with
  "budget": { "cost": 25, "time": "08:00:00" },          // RUN-05
  "permissionMode": "ask",                         // HITL-01: ask | auto | readOnly   (live)
  "approvalTimeout": "00:30:00",                   // HITL-02: then deny
  "cancelWithin": "00:00:10"                            // RUN-06
}
```

### Live settings (CFG-08)

These are the only settings that may change during a run. They are changed through the run control
interface (the CLI, or the host API), never by editing files mid-run. Each change is audited.

| Setting | Who may change it | Constraint |
|---|---|---|
| `run.permissionMode` | Owner | Changes gate decisions only; the tools offered never change (TOOL-03). |
| `run.budget.*`, `agents.*.budget.*` | Owner | Increases only. |
| Owner messages | Owner | Delivered at the agent's next iteration (HITL-03). |
| Task board contents | Owner | TASK-08. |
| Sign-off answers, approvals | Owner | HITL-02, HITL-04. |

No agent can change any setting (INV-10). None of the built-in tools can, and extension tools are
not given access to configuration.

---

## 9. Capabilities

All are off by default (CAP-01). Turning one on enables its tools, storage and settings. Each is an
object with `"enabled"` plus its own settings.

```jsonc
"capabilities": {
  "conversationStore": { "enabled": true },                         // CAP-05
  "humanInteraction": {
    "signOffs": ["planApproval", "runBudgetExceeded", "irreversibleAction"]   // HITL-04; without it, an exhausted run budget hands off
  },
  "checkpoints": { "at": ["turn", "integration"] },                 // RUN-03; also "step", "tool"
  "team":        { "maxParallelAgents": 4, "helperDepth": 2, "helperCount": 4 },
  "taskBoard":   { "maxAttempts": 3, "budget": 8 },                // TASK-09; the transitions are fixed (TASK-02), and each task says whether it needs a review
  "workspace": {
    "type": "builtin:git",
    "root": ".",
    "baseline": "main",
    "baselineChecks": ["build", "tests"],                           // WS-02
    "protectedPaths": [
      { "path": ".git/**",    "access": "hidden" },
      { "path": "**/.env*",   "access": "hidden" },                 // INV-06 note
      { "path": "sof.json",   "access": "readOnly" },           // INV-10
      { "path": "sof.*.json", "access": "readOnly" }
    ],
    "keepWorkingCopies": false                                      // WS-08
  },
  "sandbox": {
    "allowedHosts": ["api.nuget.org", "*.nuget.org"],               // SBX-01, network off by default
    "toolchains": ["/home/dev/.dotnet"],                            // read-only, and on the path
    "commandRules": [                                               // SBX-02; first match wins, unmatched is asked about
      { "match": "dotnet build*", "action": "allow" },
      { "match": "dotnet test*",  "action": "allow" },
      { "match": "git push*",     "action": "deny" }
    ],
    "secrets": { "developer": ["NUGET_TOKEN"] }                     // SBX-05, per role
    // Limits are constants until a case needs another value: 2 CPUs, 4 GiB, 1024 processes and
    // 10 Mi characters of output. A command's time is its tool's timeout.
  },
  "projectMemory": { "scope": "project", "maxTokens": 20000, "approveBy": "lead" },   // MEM
  "knowledge": { "enabled": false }
}
```

Dependencies checked at validation (CAP-03):

| Capability | Requires |
|---|---|
| `team` | `taskBoard` |
| `taskBoard` | nothing |
| `sandbox` | `workspace` |
| `checkpoints` | `conversationStore` |
| `projectMemory` | nothing; requires `humanInteraction` if `approveBy` is `owner` |
| A `builtin:human.ask_owner` tool | `humanInteraction` |
| `history.strategy` other than `none` across requests | `conversationStore` |

---

## 10. Policies and admission

```jsonc
"policies": {
  "permissionRules": [                              // TOOL-05 step 2, first match wins
    { "tool": "run_command", "when": { "field": "args.command", "in": ["git push", "git push --force"] }, "action": "deny", "reason": "Pushing is the owner's job" },
    { "tool": "delete_file", "action": "ask" },
    { "tool": "create_issue", "action": "route", "to": "lead" }
  ],
  "gates": ["rate-limit"],                          // gates for all tools (TOOL-05 step 3)
  "masking": {                                       // ING-02, ING-06
    "enabled": true,
    "patterns": { "email": "...", "employeeId": "EMP-[0-9]{6}" }   // replace the built-in email, phone and card patterns
  },
  "rateLimits": { "perOwner": { "permits": 10, "window": "01:00:00" }, "perTenant": { "permits": 200, "window": "1.00:00:00" } },   // ING-03
  "anonymousPermissions": []                                       // ING-05
}
```

Work from outside passes admission before it reaches an agent (ING-01): masking, then the checks in order, where the
first rejection wins: the agent's `triggers`, then the owner's and the tenant's rate limits. A rejection is a result
with its reason (ING-04). Masking applies to the work and to messages sent to agents; tools and knowledge sources
opt in with `maskResults` and `mask`. A per-run rate limit arrives with long-running runs (S19), and application
admission checks when an application needs one.

Permission rule actions are `allow`, `deny` (the default), `ask` and `route` (to the agent named in `to`).
A rule's `when` reads the tool's arguments only. A caller must also hold every permission the tool lists, and an
agent's `permissions` narrow the caller's. The caller's identity comes from the host (INV-03), never from configuration values
supplied with the work.

---

## 11. Extensions

```jsonc
"extensions": {
  "assemblies": ["./extensions/Acme.Agents.dll"]
}
```

An extension is a .NET type implementing one extension-point interface (see `../DESIGN.md` §4) and
marked with its id, for example `[Extension("Acme.CreateIssue")]`. The host can also register
extensions in code. Configuration refers to an extension as `extension:<id>` and passes `settings`.
Each extension declares a settings type, which validation checks like any other section. An unknown
id, or an extension of the wrong kind for the setting, is a validation error (CFG-06).

---

## 12. Operations

```jsonc
"operations": {
  "storage": {                                       // STO-01
    "type": "builtin:sqlite",                        // builtin:sqlite | builtin:memory | extension:<id>
    "path": ".sof/state.db",
    "artifacts": ".sof/artifacts"
  },
  "secrets": { "source": "builtin:environment" },    // builtin:environment | builtin:file | extension:<id>
  "retention": {                                     // PRIV-01
    "conversations": "90.00:00:00", "runRecords": "365.00:00:00", "artifacts": "90.00:00:00", "events": "30.00:00:00", "audit": "365.00:00:00"
  },
  "events": {                                        // EVT-05
    "store": ["status", "tool", "approval", "message", "handoff", "budget", "cost", "fileChange"],
    "storeModelText": false
  },
  "telemetry": {                                     // OBS-01…03
    "traces": true, "metrics": true,
    "logContent": false,
    "cacheHitWarning": 0.7                           // COST-01
  }
}
```

The coding team CLI keeps `.sof/` in the project root and adds it to `.gitignore`. The
workspace hides it from agents.

---

## 13. Layering and merging (CFG-04)

The CLI loads configuration with Microsoft.Extensions.Configuration. Layers, lowest to highest:

```
code defaults (Options classes) < sof.json < sof.<environment>.json < environment variables < command-line options
```

- **Environment variables** use the form `SOF__agents__developer__budget__turn__cost=8`.
- **Command-line options** are what the CLI passes, for example `--budget 40`.
- **Setting names match ignoring case**, in files and variables alike.
- **Unknown settings are ignored.**
- **Presets and `extends`** (between files or agent definitions) are not available yet; they arrive with the coding team.

Merge rules:

| Value | Rule |
|---|---|
| Object | Merged key by key |
| Named map (`agents`, `tools`, …) | Merged by name; a new name adds an entry |
| List | Merged by position, so set a list in one layer only |
| `null` | Makes a setting that may be unset unset. It does not remove other values, and it never removes a budget. |

`sof config show` lists every effective setting, defaults included. With `--origin`, each one also shows
its source: the file, environment variable or option that set it, or `code default, core 0.1.0`.

---

## 14. Validation (CFG-06)

Validation runs in full before anything runs, and it reports every error it can find at once. Each
error names the setting path, the problem and the fix, and the file, variable or option that set it.
A value that cannot be converted to its type (text where a number belongs, for example) is reported
one at a time, with the binder's message; the rest of the configuration then uses its defaults until it
is fixed. For example:

```
agents.developer.tools[2]: tool set "shel" does not exist. Did you mean "shell"?
tools.create_issue: write tool has no gate of its own. Add "gates": [...] or "gateExemption": "<reason>".
agents.lead.instructions: placeholder {{caller.id}} is not allowed in the stable prefix. Move it to context.operatingFacts (CTX-02, CFG-14).
```

Validation runs in this order:

| # | Phase | Rejects |
|---|---|---|
| 1 | Parse | Invalid JSON, unknown `formatVersion` |
| 2 | Shape | Wrong types, values outside allowed ranges, missing required settings (unknown settings are ignored) |
| 3 | Merge | Cycles in `extends`, missing presets (with presets) |
| 4 | References | Missing models, tools, tool sets, gates, checks, knowledge sources, agents, tool servers, extensions, secrets (by name only; secrets are not read), unknown placeholders |
| 5 | Capabilities | Capabilities used but not enabled, unmet dependencies (§9) |
| 6 | Provider | Settings or features the model or platform does not support (MDL-06), fallbacks that cannot serve their slots (MDL-04), models without prices when a cost budget is set |
| 7 | Tools | Duplicate names after merging MCP tools, invalid input formats, write tools without gates or exemptions (TOOL-02) |
| 8 | Conditions | Paths that do not exist in the schema they read, conditions on free text (INV-01), router mappings that cannot be checked |
| 9 | Prefix | Volatile placeholders in the stable prefix, and anything else that would make the prefix depend on the caller, work or time (CTX-02) |
| 10 | Invariants | Any attempt to weaken INV-01…10 (below) |

Attempts to weaken an invariant, and how each is rejected:

| Invariant | Rejected configuration |
|---|---|
| INV-01 | A condition on free-text output; a router with no structured classification |
| INV-02, INV-03 | Any setting that sets a tool's identity or grants permissions beyond the owner's (the setting does not exist, so it fails as unknown); agent `permissions` that are not a subset of the run's |
| INV-04 | A write tool with neither gates nor an exemption with a reason |
| INV-05 | Any setting that would switch audit off for write tools (`operations.retention.audit` can shorten retention, not remove entries before it) |
| INV-06 | A secret used as a literal; a secret placeholder in instructions |
| INV-07 | A budget of zero or less, zero-limit loops, or "unlimited"; `null` cannot remove a budget |
| INV-08 | Settings that would place tool or document content in instructions (none exist) |
| INV-09 | `onCheckFailure: accept`, or a task transition to `done` that skips its checks |
| INV-10 | Workspace settings that expose the configuration files to agents as writable |

Each error is reported once: a setting rejected in an early phase is not reported again by later
phases. `sof config validate` runs all phases and exits non-zero on any error. `sof config dry-run
[--agent <name>] [--input <text>] --reply <text>…` also runs an agent against a scripted model (CFG-12).

---

## 15. Presets

A preset is a configuration file shipped with the core and used through `extends`. It is an
ordinary lower layer, so an application overrides it with the same merge rules. v1 ships three
presets (CFG-11).

**`preset:single-call-extractor`**
- One agent with the `singleCall` pattern and structured output.
- No history.
- No capabilities.

**`preset:tool-using-assistant`**
- One agent with the `toolLoop` pattern and a conversation store.
- Human interaction on, in `ask` mode.
- The application adds its own tools.

**`preset:coding-team`** sets:
- **Capabilities:** team, task board, workspace (git), sandbox, project memory, checkpoints,
  conversation store, and human interaction (CLI channel).
- **Masking:** off (ING-02).
- **Sign-offs:** plan approval, run budget exceeded, irreversible action.
- **Roles:**

| Role | Model (effort) | Tools | Job |
|---|---|---|---|
| `lead` | `claude-opus-5-5` (high) | read-only files, tasks, team, record, memory, human | Plans, assigns, reviews results, re-plans. Has no write tools of its own. |
| `developer` (max 3) | `claude-opus-5-5` (high) | files, shell, record, tasks (own task) | Implements a task in its working copy until its checks pass. |
| `reviewer` (max 1) | `claude-opus-5-5` (medium) | read-only files, `tasks.review` | Reviews another agent's task. Never the author. |

- **Checks:** `build` and `tests`, from `project.values.buildCommand` and
  `project.values.testCommand`, used as task verification and baseline checks.
- **Budgets:** the run defaults from the requirements; a task budget of $8.

An application using the coding team preset needs only:

```json
{
  "$schema": "https://…/officina.schema.json",
  "formatVersion": 1,
  "extends": ["preset:coding-team"],
  "project": {
    "name": "invoice-api",
    "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" }
  },
  "capabilities": {
    "sandbox": { "allowedHosts": ["api.nuget.org", "*.nuget.org"] }
  }
}
```

The CLI's `sof init` writes this file, asking only for the build and test commands. If the
project already has them, for example in a `.csproj` or `package.json`, it detects them.

---

## 16. Requirement coverage

| Requirement | Section |
|---|---|
| CFG-01 | §7 |
| CFG-02 | §1 |
| CFG-03 | §3 |
| CFG-04, CFG-05 | §13 |
| CFG-06 | §14 |
| CFG-07 | The resolved configuration is stored with each run (`../DESIGN.md` §8) |
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
