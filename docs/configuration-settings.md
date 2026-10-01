# Officina — Settings reference

Generated from the Options classes by the test that keeps this file current; do not edit it by hand.
It lists the settings the code has today. Settings that later slices add are specified in the draft
[configuration reference](configuration-reference.md), which also describes layering (§13) and validation (§14).

## Top level

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `$schema` | text |  | The JSON Schema of the file, for editor completion. Files only. |  |
| `extends` | list |  | Other files, relative to this one, that this file builds on. Each is a lower layer; later entries override earlier ones. Files only. | `["base.json"]` |
| `formatVersion` | whole number | `1` | The configuration format version. Unknown versions are rejected. | `1` |
| `project` | section | `{"values":{}}` | The project's identity, and values usable in placeholders (CFG-14). | `{"name":"invoice-api"}` |
| `providers` | named entries | `{"claude":{"apiKey":{"secret":"ANTHROPIC_API_KEY"}}}` | Model providers, by name. | `{"claude":{"apiKey":{"secret":"ANTHROPIC_API_KEY"}}}` |
| `models` | named entries | `{"default":{"provider":"claude","model":"claude-opus-5-5","toolChoice":"auto","settings":{},"fallbacks":[]}}` | Model profiles, by name. Agents refer to them by name (MDL-02, MDL-03). | `{"strong":{"effort":"high"}}` |
| `agents` | named entries | `{}` | Agent definitions, by name (CFG-01). | `{"extractor":{"instructions":"Extract the invoice number."}}` |
| `run` | section | `{"budget":{"cost":25,"time":"8h"},"permissionMode":"ask"}` | Defaults for every run. | `{"permissionMode":"ask"}` |

## `project`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `name` | text |  | The project's name. Usable as `{{project.name}}`. | `"invoice-api"` |
| `values` | named entries | `{}` | Free-form text values. Usable as `{{project.values.<name>}}`. | `{"testCommand":"dotnet test"}` |

## `providers.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `apiKey` | section |  | The provider's credential, as the name of a secret. A literal key is rejected (CFG-09). | `{"secret":"ANTHROPIC_API_KEY"}` |

## `models.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `provider` | text | `"claude"` | The provider that serves this profile, by its name in `providers`. | `"claude"` |
| `model` | text | `"claude-opus-5-5"` | The provider's model id. | `"claude-opus-5-5"` |
| `effort` | text |  | Reasoning effort, such as `low`, `medium` or `high`. Unset uses the provider's default. | `"high"` |
| `maxOutputTokens` | whole number, ≥ 1 |  | The most tokens one reply may have. Unset uses the provider's default. | `64000` |
| `toolChoice` | text | `"auto"` | Whether the model may call tools: `auto` or `none`. | `"auto"` |
| `settings` | section | `{}` | Any other setting the provider declares for the model, such as a temperature (MDL-02). | `{"temperature":0.2}` |
| `fallbacks` | list | `[]` | Profiles to use instead, in order, when this one is unavailable (MDL-04). | `["backup"]` |

## `agents.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `extends` | text |  | Another agent definition this one builds on (CFG-05). Its settings are the lower layer; cycles are rejected. Files only. | `"base-coder"` |
| `description` | text |  | What the agent is for. Usable in instructions as `{{agent.description}}`. | `"Implements one task."` |
| `instructions` | text |  | The agent's job. Required: only the application knows it (CFG-17). Placeholders may use the `project` and `agent` namespaces only (CFG-14). | `"Extract the invoice number, date and total. Reply as JSON."` |
| `model` | text | `"default"` | The name of the model profile the agent runs on (MDL-03). | `"strong"` |

## `run`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `budget` | section | `{"cost":25,"time":"8h"}` | The run's budget. It can be high, but never removed or unlimited (RUN-05). Live: the owner may change it during a run. Cannot be removed or unlimited (INV-07). | `{"cost":25,"time":"8h"}` |
| `permissionMode` | `"ask"`, `"auto"`, `"readOnly"` | `"ask"` | How tool calls that need permission are decided: `ask` the owner, `auto` by the rules, or `readOnly` (HITL-01). Live: the owner may change it during a run. | `"ask"` |

## `providers.<name>.apiKey`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `secret` | text |  | The secret's name: letters, digits and underscores. | `"ANTHROPIC_API_KEY"` |

## `run.budget`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `cost` | number, > 0 | `25` | The most the run may spend, in USD. Live: the owner may change it during a run. Cannot be removed or unlimited (INV-07). | `25` |
| `time` | duration (`ms`, `s`, `m`, `h`, `d`) | `"8h"` | The longest the run may take: a number with a unit, `ms`, `s`, `m`, `h` or `d`. Live: the owner may change it during a run. Cannot be removed or unlimited (INV-07). | `"8h"` |
