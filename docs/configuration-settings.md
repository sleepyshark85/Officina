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
| `tools` | named entries | `{}` | Tools, by the name the model sees. | `{"create_issue":{"source":"extension:Acme.CreateIssue","gates":["issue-dedupe"]}}` |
| `toolSets` | named entries | `{}` | Named groups of tools, by name in `tools`. Agents are offered tools by tool set. | `{"issues":["create_issue","find_issue"]}` |
| `gates` | named entries | `{}` | Gates, by name. Tools and policies refer to them by name. | `{"issue-dedupe":{"use":"extension:Acme.IssueDedupeGate"}}` |
| `policies` | section | `{"permissionRules":[],"gates":[],"anonymousPermissions":[]}` | Permission rules, gates for all tools, and anonymous callers' permissions. | `{"gates":["no-main-branch"]}` |
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
| `tools` | list | `[]` | The tool sets, by name in `toolSets`, whose tools the agent is offered. The same tools are offered whoever the caller is. | `["files","issues"]` |
| `permissions` | list |  | Narrows the caller's permissions for this agent's tool calls: a permission counts only if the caller holds it and it is listed here. Unset keeps the caller's. | `["issues:write"]` |
| `maxParallelToolCalls` | whole number, ≥ 1 | `4` | The most tool calls from one reply that run at the same time, when every tool called is safe to run in parallel. | `4` |

## `tools.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `source` | text |  | Where the tool comes from: `extension:<id>` for a tool the application registers, or `provider:<name>` for a tool the model provider runs itself. Required. | `"extension:Acme.CreateIssue"` |
| `kind` | `"read"`, `"write"` |  | `write` for a tool that changes something. Unset uses the tool's declaration; a tool that declares itself `write` stays `write`. | `"write"` |
| `permissions` | list | `[]` | Permissions the caller must hold to call the tool. | `["issues:write"]` |
| `gates` | list | `[]` | The tool's own gates, by name in `gates`. They run after the gates for all tools. | `["issue-dedupe"]` |
| `gateExemption` | text |  | Why a write tool needs no gate of its own. Every write tool needs gates or this reason. | `"Writes only to a scratch folder."` |
| `approval` | `"never"`, `"always"` |  | Whether each call needs a human's approval. Unset means `always` for irreversible tools, otherwise `never`. For approval by rule, give the tool a `builtin:require-approval` gate with a condition. | `"always"` |
| `timeout` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"00:02:00"` | The longest one call may take, as `hh:mm:ss`. | `"00:15:00"` |
| `maxAttempts` | whole number, ≥ 1 | `1` | How many times a call is tried. Only timeouts and unavailable errors are retried, and irreversible tools never are. | `3` |
| `maxResultLength` | whole number, ≥ 1 | `32000` | The most characters of a result that enter the conversation; the rest is cut off. | `8000` |
| `parallelSafe` | boolean |  | Whether calls may run at the same time as other calls. Unset uses the tool's declaration; `true` cannot loosen a tool that declares itself unsafe. Irreversible tools never run in parallel. | `false` |
| `irreversible` | boolean | `false` | Whether the tool's effects cannot be undone. Such a tool is a write tool, is carried out at most once for the same run and arguments, and needs approval unless `approval` says otherwise. | `true` |
| `reason` | text |  | Why a provider tool is enabled. Required for `provider:` tools. | `"The lead researches unfamiliar libraries."` |

## `gates.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `use` | text |  | What the gate runs: `builtin:require-approval` or `builtin:deny`, which act when `when` holds, or `extension:<id>` for a gate the application registers. Required. | `"builtin:require-approval"` |
| `when` | section |  | For the built-in gates: the condition over the tool's arguments under which the gate acts. Unset means always. | `{"field":"args.branch","in":["main","master"]}` |

## `policies`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `permissionRules` | list | `[]` | Permission rules, in order. The first that matches a call decides it; a call no rule matches goes on. | `[{"tool":"delete_file","action":"ask"}]` |
| `gates` | list | `[]` | Gates, by name in `gates`, that run before every tool call, ahead of the tool's own gates. | `["no-main-branch"]` |
| `anonymousPermissions` | list | `[]` | The permissions a caller without an identity holds. | `["issues:read"]` |

## `run`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `budget` | section | `{"cost":25,"time":"08:00:00"}` | The run's budget. It can be high, but it cannot be removed or unlimited. Required. Live: the owner may change it during a run. | `{"cost":25,"time":"08:00:00"}` |
| `permissionMode` | `"ask"`, `"auto"`, `"readOnly"` | `"ask"` | How tool calls that need permission are decided: `ask` the owner, `auto` by the rules, or `readOnly`. Live: the owner may change it during a run. | `"ask"` |

## `providers.<name>.apiKey`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `secret` | text |  | The name of the secret, such as the environment variable that holds it. Required. | `"ANTHROPIC_API_KEY"` |

## `gates.<name>.when`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `field` | text |  | The field a test reads: a dotted path with optional `[n]` indexes, such as `args.branch`. | `"args.branch"` |
| `is` | text |  | Holds when the field equals this value. Numbers compare by value, and `true` and `false` match booleans. | `"main"` |
| `in` | list |  | Holds when the field equals one of these values. | `["main","master"]` |
| `gt` | number |  | Holds when the field is a number greater than this. | `10` |
| `gte` | number |  | Holds when the field is a number greater than or equal to this. | `0.8` |
| `lt` | number |  | Holds when the field is a number less than this. | `10` |
| `lte` | number |  | Holds when the field is a number less than or equal to this. | `100` |
| `exists` | boolean |  | `true` holds when the field is present and not null; `false` when it is missing or null. | `true` |
| `all` | list |  | Holds when every one of these conditions holds. | `[{"field":"args.force","is":"true"}]` |
| `any` | list |  | Holds when at least one of these conditions holds. | `[{"field":"args.force","is":"true"}]` |
| `not` | section |  | Holds when this condition does not. | `{"field":"args.draft","is":"true"}` |

## `policies.permissionRules[]`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `tool` | text |  | The tool the rule applies to, by name in `tools`. Required. | `"run_command"` |
| `when` | section |  | The condition over the tool's arguments under which the rule matches, written as in `gates.<name>.when`. Unset matches every call. | `{"field":"args.command","in":["git push"]}` |
| `action` | `"allow"`, `"deny"`, `"ask"`, `"route"` | `"deny"` | What a match decides: `allow`, `deny`, `ask` a human, or `route` to `to`. | `"deny"` |
| `reason` | text |  | Why, as told to the model and the human. | `"Only the owner pushes."` |
| `to` | text |  | For `route`: the agent, by name, that the turn is handed to. | `"lead"` |

## `run.budget`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `cost` | number, > 0 | `25` | The most the run may spend, in USD. Live: the owner may change it during a run. | `25` |
| `time` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"08:00:00"` | The longest the run may take, as `hh:mm:ss` or `d.hh:mm:ss`. Live: the owner may change it during a run. | `"08:00:00"` |
