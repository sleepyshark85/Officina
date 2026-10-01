# Officina — Settings reference

Generated from the Options classes in `Sleepyshark.Officina.Core` by the test that keeps this file
current; do not edit it by hand. It lists the settings the code has today. Settings that later slices
add are specified in the draft [configuration reference](configuration-reference.md).

How the layers merge, the value forms and validation are described in the configuration reference
(§2, §13, §14). **Live** settings may be changed by the owner during a run (CFG-08). In files, `null`
removes a value so the code default applies, except where it would weaken an invariant.

## Top level

Every section is optional. Files may also use the keys marked *Files only*.

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `$schema` | text | none | The JSON Schema of the file, for editor completion. Ignored when loading. Files only. | `"https://raw.githubusercontent.com/sleepyshark85/Officina/main/docs/officina.schema.json"` |
| `extends` | list of text | none | Presets (`preset:<id>`) and other files (paths relative to this one) this file builds on. Each is a lower layer; later entries override earlier ones. Files only. | `["preset:coding-team"]` |
| `formatVersion` | whole number, only 1 | `1` | The configuration format version. Unknown versions are rejected. | `1` |
| `project` | section | see below | The project's identity, and values usable in placeholders (CFG-14). | `{ "name": "invoice-api" }` |
| `providers` | named entries, each section | `{"claude":{"type":"claude","apiKey":{"secret":"ANTHROPIC_API_KEY"}}}` | Model providers, by name. | `{ "claude": { "type": "claude", "apiKey": { "secret": "ANTHROPIC_API_KEY" } } }` |
| `models` | named entries, each section | `{"default":{"provider":"claude","model":"claude-opus-5-5","toolChoice":"auto","settings":{},"fallbacks":[]}}` | Model profiles, by name. Agents refer to them by name (MDL-02, MDL-03). | `{ "strong": { "model": "claude-opus-5-5", "effort": "high" } }` |
| `agents` | named entries, each section | `{}` | Agent definitions, by name (CFG-01). | `{ "extractor": { "instructions": "Extract the invoice number." } }` |
| `run` | section | see below | Defaults for every run. | `{ "permissionMode": "ask" }` |
| `capabilities` | see [Capabilities](#capabilities) | `{}` | Optional capabilities, by name. All are off by default (CAP-01). | `{ "conversationStore": true }` |

## `project`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `name` | text | unset | The project's name. Usable as `{{project.name}}`. | `"invoice-api"` |
| `values` | named entries, each text | `{}` | Free-form text values. Usable as `{{project.values.<name>}}`. | `{ "buildCommand": "dotnet build", "testCommand": "dotnet test" }` |

## `providers.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `type` | text | `"claude"` | The provider implementation. | `"claude"` |
| `apiKey` | `{ "secret": "NAME" }` | unset | The provider's credential, as the name of a secret. A literal key is rejected (CFG-09). | `{ "secret": "ANTHROPIC_API_KEY" }` |
| `baseUrl` | text | unset | Overrides the provider's endpoint. Unset uses the provider's own. | `"https://llm-proxy.example.com"` |

## `models.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `provider` | text | `"claude"` | The provider that serves this profile, by its name in `providers`. | `"claude"` |
| `model` | text | `"claude-opus-5-5"` | The provider's model id. | `"claude-opus-5-5"` |
| `effort` | text | unset | Reasoning effort, from the values the provider declares, such as `low`, `medium`, `high`, `xhigh` or `max`. Unset uses the provider's default. | `"high"` |
| `maxOutputTokens` | whole number, ≥ 1 | unset | The most tokens one reply may have. Unset uses the provider's default. | `64000` |
| `toolChoice` | text | `"auto"` | Whether the model may call tools: `auto` or `none`. Other modes only where the provider declares them. | `"auto"` |
| `settings` | named entries, each any JSON value | `{}` | Any other setting the provider declares for the model, such as a temperature. Checked against the provider's declaration (MDL-02). | `{ "thinkingDisplay": "summarized" }` |
| `fallbacks` | list of text | `[]` | Profiles to use instead, in order, when this one is unavailable (MDL-04). | `["strong-backup"]` |

## `agents.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `extends` | text | none | Another agent definition this one builds on (CFG-05). Its settings are the lower layer; cycles are rejected. Files only. | `"base-coder"` |
| `description` | text | unset | What the agent is for. Usable in instructions as `{{agent.description}}`. | `"Implements one task in its own working copy."` |
| `instructions` | text, or `{ "file": "path" }` | none (required) | The agent's job, in its own words. Required: only the application knows it (CFG-17). Placeholders may use the `project` and `agent` namespaces only (CFG-14). **Required.** | `"Extract the invoice number, date and total. Reply as JSON."` |
| `model` | profile name, or a profile inline (as `models.<name>`) | `"default"` | The model profile of the agent's default model slot: a profile name, or a profile written inline (MDL-03). | `"strong"` |
| `capabilities` | list of text | unset | Which of the application's enabled capabilities the agent uses. Unset means all of them (CAP-01, CAP-03). | `["workspace", "sandbox"]` |

## `run`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `budget` | section | see below | The run's budget. Every run has one; it can be high, but never unlimited (RUN-05, INV-07). **Live.** Cannot be removed or made unlimited (INV-07). | `{ "cost": 25, "time": "8h" }` |
| `permissionMode` | `"ask"`, `"auto"`, `"readOnly"` | `"ask"` | How tool calls that need permission are decided: `ask` the owner, decide `auto`matically by the rules, or allow `readOnly` tools only (HITL-01). **Live.** | `"ask"` |

## `run.budget`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `cost` | number, > 0 | `25` | The most the run may spend, in the currency of the price table (USD by default). **Live.** Cannot be removed or made unlimited (INV-07). | `25` |
| `time` | duration (`ms`, `s`, `m`, `h`, `d`), > 0 | `"8h"` | The longest the run may take. **Live.** Cannot be removed or made unlimited (INV-07). | `"8h"` |

## Capabilities

No capability is available in this build yet. Each capability adds its section here when it ships.
