# Officina — Settings reference

Generated from the Options classes by the test that keeps this file current; do not edit it by hand.
It lists the settings the code has today. Settings that later slices add are specified in the draft
[configuration reference](configuration-reference.md), which also describes layering (§13) and validation (§14).

## Top level

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `$schema` | text |  | The JSON Schema of the file, for editor completion. Files only. |  |
| `formatVersion` | whole number | `1` | The configuration format version. Unknown versions are rejected. | `1` |
| `project` | section | `{"values":{}}` | The project's identity, and values usable in placeholders. | `{"name":"invoice-api"}` |
| `providers` | named entries | `{"claude":{"apiKey":{"secret":"ANTHROPIC_API_KEY"}}}` | Model providers, by name. | `{"claude":{"apiKey":{"secret":"ANTHROPIC_API_KEY"}}}` |
| `models` | named entries | `{"default":{"provider":"claude","model":"claude-opus-5-5","toolChoice":"auto","settings":{}}}` | Model profiles, by name. Agents refer to them by name. | `{"strong":{"effort":"high"}}` |
| `agents` | named entries | `{}` | Agent definitions, by name. | `{"extractor":{"instructions":"Extract the invoice number."}}` |
| `run` | section | `{"budget":{"cost":25,"time":"08:00:00"},"permissionMode":"ask"}` | Defaults for every run. | `{"permissionMode":"ask"}` |

## `project`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `name` | text |  | The project's name. Usable in instructions as `{{project.name}}`. | `"invoice-api"` |
| `values` | named entries | `{}` | Free-form text values. Usable in instructions as `{{project.values.<name>}}`. | `{"testCommand":"dotnet test"}` |

## `providers.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `apiKey` | section |  | The provider's credential, as the name of a secret, which is read when it is used. | `{"secret":"ANTHROPIC_API_KEY"}` |

## `models.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `provider` | text | `"claude"` | The provider that serves this profile, by its name in `providers`. Required. | `"claude"` |
| `model` | text | `"claude-opus-5-5"` | The provider's model id. Required. | `"claude-opus-5-5"` |
| `effort` | text |  | Reasoning effort, such as `low`, `medium` or `high`. Unset uses the provider's default. | `"high"` |
| `maxOutputTokens` | whole number, ≥ 1 |  | The most tokens one reply may have. Unset uses the provider's default. | `64000` |
| `toolChoice` | `"auto"`, `"none"` | `"auto"` | Whether the model may call tools. | `"auto"` |
| `settings` | named entries | `{}` | Any other setting the provider declares for the model, such as a temperature, as text. | `{"temperature":"0.2"}` |

## `agents.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `extends` | text |  | Another agent definition this one builds on: it inherits every setting it does not set itself. Files only. | `"base-coder"` |
| `description` | text |  | What the agent is for. Usable in instructions as `{{agent.description}}`. | `"Implements one task."` |
| `instructions` | text |  | The agent's job. Only the application knows it, so it has no default. Placeholders may use the project and the agent only. Required. | `"Extract the invoice number, date and total. Reply as JSON."` |
| `model` | text | `"default"` | The name of the model profile the agent runs on. Required. | `"strong"` |

## `run`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `budget` | section | `{"cost":25,"time":"08:00:00"}` | The run's budget. It can be high, but it cannot be removed or unlimited. Required. Live: the owner may change it during a run. | `{"cost":25,"time":"08:00:00"}` |
| `permissionMode` | `"ask"`, `"auto"`, `"readOnly"` | `"ask"` | How tool calls that need permission are decided: `ask` the owner, `auto` by the rules, or `readOnly`. Live: the owner may change it during a run. | `"ask"` |

## `providers.<name>.apiKey`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `secret` | text |  | The name of the secret, such as the environment variable that holds it. Required. | `"ANTHROPIC_API_KEY"` |

## `run.budget`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `cost` | number, > 0 | `25` | The most the run may spend, in USD. Live: the owner may change it during a run. | `25` |
| `time` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"08:00:00"` | The longest the run may take, as `hh:mm:ss` or `d.hh:mm:ss`. Live: the owner may change it during a run. | `"08:00:00"` |
