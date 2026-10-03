# Officina — Settings reference

Generated from the Options classes by the test that keeps this file current; do not edit it by hand.
It lists the settings the code has today. Settings that later slices add are specified in the draft
[configuration reference](configuration-reference.md), which also describes layering (§13) and validation (§14).

## Top level

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `$schema` | text |  | The JSON Schema of the file, for editor completion. Files only. |  |
| `extends` | list |  | Presets, as `preset:<id>`, and other files, relative to this one, that this file builds on: each a layer below it, lowest first. A list or a value here replaces theirs. Files only. | `["preset:coding-team"]` |
| `formatVersion` | whole number | `1` | The configuration format version. Unknown versions are rejected. | `1` |
| `project` | section | `{"values":{}}` | The project's identity, and values usable in placeholders. | `{"name":"invoice-api"}` |
| `providers` | named entries | `{"claude":{"apiKey":{"secret":"ANTHROPIC_API_KEY"},"prices":{"claude-fable-5-1":{"input":10,"output":50,"cacheRead":0.25,"cacheWrite5m":12.5,"cacheWrite1h":20},"claude-opus-5-5":{"input":4,"output":20,"cacheRead":0.2,"cacheWrite5m":5,"cacheWrite1h":8},"claude-opus-5":{"input":5,"output":25,"cacheRead":0.5,"cacheWrite5m":6.25,"cacheWrite1h":10},"claude-opus-4-8":{"input":5,"output":25,"cacheRead":0.5,"cacheWrite5m":6.25,"cacheWrite1h":10},"claude-sonnet-5-5":{"input":2,"output":10,"cacheRead":0.2,"cacheWrite5m":2.5,"cacheWrite1h":4},"claude-sonnet-5":{"input":2,"output":10,"cacheRead":0.2,"cacheWrite5m":2.5,"cacheWrite1h":4},"claude-haiku-4-5":{"input":1,"output":5,"cacheRead":0.1,"cacheWrite5m":1.25,"cacheWrite1h":2}},"timeout":"00:10:00","retry":{"maxAttempts":5,"initialDelay":"00:00:01","maxDelay":"00:01:00"},"features":{"structuredOutput":false,"clearToolResults":false,"refusalFallback":false}}}` | Model providers, by name. | `{"claude":{"apiKey":{"secret":"ANTHROPIC_API_KEY"}}}` |
| `models` | named entries | `{"default":{"provider":"claude","model":"claude-opus-5-5","toolChoice":"auto","settings":{},"fallbacks":[]}}` | Model profiles, by name. Agents refer to them by name. | `{"strong":{"effort":"high"}}` |
| `agents` | named entries | `{}` | Agent definitions, by name. | `{"extractor":{"instructions":"Extract the invoice number."}}` |
| `toolServers` | named entries | `{}` | External tool servers (MCP), by name. Tools use their tools with `mcp:<server>/<tool>` sources. | `{"github":{"command":"github-mcp-server","args":["stdio"]}}` |
| `tools` | named entries | `{}` | Tools, by the name the model sees. | `{"create_issue":{"source":"extension:Acme.CreateIssue","gates":["issue-dedupe"]}}` |
| `toolSets` | named entries | `{}` | Named groups of tools, by name in `tools`. Agents are offered tools by tool set. | `{"issues":["create_issue","find_issue"]}` |
| `gates` | named entries | `{}` | Gates, by name. Tools and policies refer to them by name. | `{"issue-dedupe":{"use":"extension:Acme.IssueDedupeGate"}}` |
| `policies` | section | `{"permissionRules":[],"gates":[],"anonymousPermissions":[],"masking":{"enabled":true},"rateLimits":{}}` | Permission rules, gates for all tools, anonymous callers' permissions, masking and rate limits. | `{"gates":["no-main-branch"]}` |
| `checks` | named entries | `{}` | Checks of output, by name. Agents refer to them by name in `output.checks`. | `{"no-secrets":{"use":"extension:Acme.NoSecretsCheck"}}` |
| `knowledge` | named entries | `{}` | Knowledge sources, by name. | `{"handbook":{"use":"extension:Acme.HandbookIndex"}}` |
| `run` | section | `{"budget":{"cost":25,"time":"08:00:00"},"permissionMode":"ask","approvalTimeout":"00:30:00","cancelWithin":"00:00:10"}` | Defaults for every run. | `{"permissionMode":"ask"}` |
| `operations` | section | `{"telemetry":{"cacheHitWarning":0.7},"storage":{"path":".sof/sof.db"}}` | How the engine is operated. | `{"telemetry":{"cacheHitWarning":0.7}}` |
| `storage` | section | `{"retention":{"audit":"365.00:00:00"}}` | What is stored, and for how long. | `{"unstoredEvents":["textGenerated"],"retention":{"events":"30.00:00:00"}}` |
| `capabilities` | section | `{"conversationStore":{"enabled":false},"knowledge":{"enabled":false},"humanInteraction":{"enabled":false},"workspace":{"enabled":false,"protectedPaths":[],"baselineChecks":[],"keepWorkingCopies":false},"sandbox":{"enabled":false,"allowedHosts":[],"toolchains":[],"commandRules":[],"secrets":{}},"taskBoard":{"enabled":false,"maxAttempts":3,"budget":{"cost":8}},"projectMemory":{"enabled":false,"scope":"project","maxTokens":20000,"approveBy":"lead"},"checkpoints":{"enabled":false},"team":{"enabled":false,"helperDepth":2,"helperCount":4}}` | Optional capabilities and their settings. All are off by default. | `{"conversationStore":{"enabled":true}}` |

## `project`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `name` | text |  | The project's name. Usable in instructions as `{{project.name}}`. | `"invoice-api"` |
| `values` | named entries | `{}` | Free-form text values. Usable in instructions as `{{project.values.<name>}}`. | `{"testCommand":"dotnet test"}` |

## `providers.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `apiKey` | section |  | The provider's credential, as the name of a secret, which is read when it is used. | `{"secret":"ANTHROPIC_API_KEY"}` |
| `prices` | named entries | `{}` | Prices per million tokens, by model id, for reporting cost and enforcing cost budgets. Every model a profile uses needs one; the `claude` provider ships the prices of current models, and a configured price overrides the shipped values it sets. | `{"claude-opus-5-5":{"input":4,"output":20,"cacheRead":0.2,"cacheWrite5m":5,"cacheWrite1h":8}}` |
| `timeout` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"00:10:00"` | How long to wait for the provider to answer a call, and then for each next piece of its streamed reply, as `hh:mm:ss`. A call that goes quiet for longer is given up on as a transient failure and retried. | `"00:10:00"` |
| `retry` | section | `{"maxAttempts":5,"initialDelay":"00:00:01","maxDelay":"00:01:00"}` | How failed calls are retried, for every agent that calls this provider. | `{"maxAttempts":5,"initialDelay":"00:00:01"}` |
| `maxConcurrentCalls` | whole number, ≥ 1 |  | The most calls to this provider in flight at once, across all agents: the account's share of the provider's rate limit. Calls over it wait their turn, the team lead's first. Unset means no limit. | `8` |
| `features` | section | `{"structuredOutput":false,"clearToolResults":false,"refusalFallback":false}` | Features of the provider's own API, each off until switched on. A feature switched on for a model that does not have it is a configuration error. | `{"structuredOutput":true,"refusalFallback":true}` |

## `models.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `provider` | text | `"claude"` | The provider that serves this profile, by its name in `providers`. Required. | `"claude"` |
| `model` | text | `"claude-opus-5-5"` | The provider's model id. Required. | `"claude-opus-5-5"` |
| `effort` | text |  | Reasoning effort, such as `low`, `medium` or `high`. Unset uses the provider's default. | `"high"` |
| `maxOutputTokens` | whole number, ≥ 1 |  | The most tokens one reply may have. Unset uses the provider's default. | `64000` |
| `toolChoice` | `"auto"`, `"none"` | `"auto"` | Whether the model may call tools. | `"auto"` |
| `settings` | named entries | `{}` | Any other setting the provider declares for the model, such as a temperature, as text. | `{"temperature":"0.2"}` |
| `fallbacks` | list | `[]` | Other profiles, by name in `models`, to use in order when this one stays unavailable or overloaded after its retries. Each is offered the same tools as the slot, so it must support them. A fallback's own fallbacks are not used. Using one is recorded. | `["fast"]` |

## `agents.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `extends` | text |  | Another agent definition this one builds on: it inherits every setting it does not set itself. Files only. | `"base-coder"` |
| `description` | text |  | What the agent is for. Usable in instructions as `{{agent.description}}`. | `"Implements one task."` |
| `instructions` | text |  | The agent's job. Only the application knows it, so it has no default. Placeholders may use the project and the agent only. Required. | `"Extract the invoice number, date and total. Reply as JSON."` |
| `model` | text | `"default"` | The name of the model profile the agent runs on. Required. | `"strong"` |
| `triggers` | list |  | How work may reach the agent: `conversation`, `request`, `batch`, `schedule`, `event` or `longRunning`. Work that arrives any other way is rejected. Unset accepts every way. | `["request","batch"]` |
| `tools` | list | `[]` | The tool sets, by name in `toolSets`, whose tools the agent is offered. The same tools are offered whoever the caller is. | `["files","issues"]` |
| `toolDescriptionsOnDemand` | boolean | `false` | Whether the model is offered only the tools' names, and reads a tool's description and arguments with `describe_tool` when it needs them. For agents with many tools. | `true` |
| `permissions` | list |  | Narrows the caller's permissions for this agent's tool calls: a permission counts only if the caller holds it and it is listed here. Unset keeps the caller's. | `["issues:write"]` |
| `helpers` | list |  | The agent definitions, by name in `agents`, that this agent may start as helpers with `builtin:team.start_helper`, each for a piece of its work. Unset: none. | `["researcher"]` |
| `maxParallelToolCalls` | whole number, ≥ 1 | `4` | The most tool calls from one reply that run at the same time, when every tool called is safe to run in parallel. | `4` |
| `pattern` | section | `{"type":"toolLoop","steps":[],"next":[],"routes":{},"branches":[],"maxParallel":4,"combine":"all","checks":[],"maxRevisions":3,"maxReplans":2,"roles":{}}` | How the agent does its work: in a turn of its own, or in a pattern of steps. A pattern's steps draw on the agent's turn budget, and the agent's other settings apply to its own turns. Required. | `{"type":"router","routes":{"bug":{"agent":"developer"}}}` |
| `context` | section | `{"operatingFacts":[],"historyCacheLifetime":"00:05:00","recordScope":"all","currentTask":true,"retrieval":{"beforeTurn":[],"handOffWhenNotCovered":false},"history":{"strategy":"none","shortening":"provider","lastTurns":10}}` | How the agent's model input is built. Required. | `{"operatingFacts":["Today is {{now:date}}."]}` |
| `output` | section | `{"format":"text","attempts":2,"checks":[],"onCheckFailure":"handoff"}` | What the output must be before a turn completes with it. Required. | `{"format":"structured","schema":"{ \u0022type\u0022: \u0022object\u0022 }"}` |
| `stopWhen` | section | `{"finished":true,"checksPass":false}` | When a turn is complete. They combine: the first that holds completes the turn. Required. | `{"finished":false,"finishTool":"submit_report"}` |
| `budget` | section | `{"turn":{"iterations":50,"toolCalls":200,"tokens":3000000,"cost":5,"time":"00:45:00"}}` | The agent's budgets. They can be high, but they cannot be removed or unlimited. Required. | `{"turn":{"iterations":50,"cost":5}}` |
| `stall` | section | `{"iterationsWithoutProgress":3}` | When a turn has stalled. Required. | `{"iterationsWithoutProgress":5}` |
| `handOffOnPolicyGap` | boolean | `true` | Whether a turn ends in a handoff for a policy gap when every tool call of an iteration is refused. With `false`, the refusals go back to the model. | `false` |

## `toolServers.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `transport` | `"stdio"`, `"http"` | `"stdio"` | How the server is reached: `stdio` starts `command` and talks over its standard input and output; `http` posts to `url` (Streamable HTTP). | `"http"` |
| `command` | text |  | For `stdio`: the program that runs the server. | `"github-mcp-server"` |
| `args` | list | `[]` | For `stdio`: the program's arguments. | `["stdio"]` |
| `env` | named entries | `{}` | For `stdio`: environment variables of the program, each a secret read when the server starts. | `{"GITHUB_TOKEN":{"secret":"GITHUB_TOKEN"}}` |
| `url` | text |  | For `http`: the server's endpoint. | `"https://tracker.example.com/mcp"` |
| `headers` | named entries | `{}` | For `http`: headers sent with every request, each a secret read when the server is first reached. | `{"Authorization":{"secret":"TRACKER_AUTH"}}` |

## `tools.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `source` | text |  | Where the tool comes from: `extension:<id>` for a tool the application registers (`sof` registers `workspace.read_file`, `workspace.search`, `workspace.edit_file`, `workspace.write_file`, `workspace.delete_file` and `workspace.move_file` when the workspace is on, and `sandbox.run`, `sandbox.start_process`, `sandbox.read_process_output` and `sandbox.stop_process` when the sandbox is on), `mcp:<server>/<tool>` for a tool of a server in `toolServers`, `knowledge:<name>` to search a source in `knowledge`, `provider:<name>` for a tool the model provider runs itself, or `builtin:<name>` for a built-in tool: `record.propose_fact`, `record.propose_finding`, `record.propose_decision` and `record.cite` propose changes to the run record; `artifact.page` reads an artifact, such as a trimmed result in full; `human.ask_owner` and `human.request_handoff` reach the owner; `tasks.create`, `tasks.update`, `tasks.claim`, `tasks.submit_for_review` and `tasks.review` work on the task board; `memory.propose_change` and `memory.review` change project memory; and `team.message` and `team.start_helper` reach other agents of a team. Required. | `"extension:Acme.CreateIssue"` |
| `kind` | `"read"`, `"write"` |  | `write` for a tool that changes something. Unset uses the tool's declaration, and `write` for a tool server's tools; a tool that declares itself `write` stays `write`. | `"write"` |
| `permissions` | list | `[]` | Permissions the caller must hold to call the tool. | `["issues:write"]` |
| `gates` | list | `[]` | The tool's own gates, by name in `gates`. They run after the gates for all tools. | `["issue-dedupe"]` |
| `gateExemption` | text |  | Why a write tool needs no gate of its own. Every write tool needs gates or this reason. | `"Writes only to a scratch folder."` |
| `approval` | `"never"`, `"always"` |  | Whether each call needs a human's approval. Unset means `always` for irreversible tools, otherwise `never`. For approval by rule, give the tool a `builtin:require-approval` gate with a condition. | `"always"` |
| `timeout` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"00:02:00"` | The longest one call may take, as `hh:mm:ss`. It does not apply to `builtin:tasks.submit_for_review`, whose checks each have their own time limit. | `"00:15:00"` |
| `maxAttempts` | whole number, ≥ 1 | `1` | How many times a call is tried. Only timeouts and unavailable errors are retried, and irreversible tools never are. | `3` |
| `maxResultLength` | whole number, ≥ 1 | `32000` | The most characters of a result that enter the conversation. The full result is kept as an artifact, which a `builtin:artifact.page` tool reads. | `8000` |
| `parallelSafe` | boolean |  | Whether calls may run at the same time as other calls. Unset uses the tool's declaration; `true` cannot loosen a tool that declares itself unsafe. Irreversible tools never run in parallel. | `false` |
| `irreversible` | boolean | `false` | Whether the tool's effects cannot be undone. Such a tool is a write tool, is carried out at most once for the same run and arguments, and needs approval unless `approval` says otherwise. | `true` |
| `maskResults` | boolean | `false` | Whether personal data in the tool's results is masked before the model sees them, when masking is on. | `true` |
| `receivesMaskedValues` | boolean | `false` | Whether masked values are restored in the tool's arguments, so it receives the real ones, such as a tool that sends an email. Its results are masked again. | `true` |
| `untrusted` | boolean | `false` | Whether the tool's results are untrusted content, such as fetched web pages. An agent that has read them is marked, and gates can act on the mark. | `true` |
| `reason` | text |  | Why a provider tool is enabled. Required for `provider:` tools. | `"The lead researches unfamiliar libraries."` |
| `limits` | section |  | The limits the provider applies to a `provider:` tool, where it offers them. | `{"maxUses":5,"allowedDomains":["github.com"]}` |

## `gates.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `use` | text |  | What the gate runs: `builtin:require-approval` or `builtin:deny`, which act when `when` holds; `builtin:untrusted-content-approval`, which asks when `when` holds and the agent has read untrusted content; or `extension:<id>` for a gate the application registers (`sof` registers `extension:sandbox.commandRules`, the sandbox's command rules, when the sandbox is on). Required. | `"builtin:require-approval"` |
| `when` | section |  | For the built-in gates: the condition over the tool's arguments under which the gate acts. Unset means always. | `{"field":"args.branch","in":["main","master"]}` |

## `policies`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `permissionRules` | list | `[]` | Permission rules, in order. The first that matches a call decides it; a call no rule matches goes on. | `[{"tool":"delete_file","action":"ask"}]` |
| `gates` | list | `[]` | Gates, by name in `gates`, that run before every tool call, ahead of the tool's own gates. | `["no-main-branch"]` |
| `anonymousPermissions` | list | `[]` | The permissions a caller without an identity holds. | `["issues:read"]` |
| `masking` | section | `{"enabled":true}` | Masking of personal data in work from outside, before the model, history or logs see it. On by default. Required. | `{"enabled":false}` |
| `rateLimits` | section | `{}` | How much work each owner, each tenant and each run may take in. Required. | `{"perOwner":{"permits":20,"window":"01:00:00"}}` |

## `checks.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `use` | text |  | The check the application registers, as `extension:<id>`. Set it or `command`. | `"extension:Acme.NoSecretsCheck"` |
| `command` | text |  | A command that checks a working copy, run in the sandbox there: the check passes when it exits with 0, and its last lines of output are the findings. It needs the sandbox, and checks a task's work or the baseline, not an agent's output. Set it or `use`. | `"dotnet test"` |
| `timeout` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"00:20:00"` | For a command: the longest it may run, as `hh:mm:ss`; then it is stopped, and the check fails. | `"00:40:00"` |

## `knowledge.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `use` | text |  | The source the application registers, as `extension:<id>`. Required. | `"extension:Acme.HandbookIndex"` |
| `mask` | boolean | `false` | Whether personal data in the passages is masked before the model sees them, when masking is on. | `true` |

## `run`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `budget` | section | `{"cost":25,"time":"08:00:00"}` | The run's budget. It can be high, but it cannot be removed or unlimited. Required. Live: the owner may change it during a run. | `{"cost":25,"time":"08:00:00"}` |
| `permissionMode` | `"ask"`, `"auto"`, `"readOnly"` | `"ask"` | How tool calls that need permission are decided: `ask` the owner about every write tool call no permission rule allows, `auto` by the rules alone, or `readOnly`, where no write tool runs. Live: the owner may change it during a run. | `"ask"` |
| `approvalTimeout` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"00:30:00"` | How long an approval, sign-off or question waits for the owner, as `hh:mm:ss`. With no answer by then, an approval or sign-off is denied and a question goes unanswered. | `"00:30:00"` |
| `cancelWithin` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"00:00:10"` | How long a cancelled agent has to stop, as `hh:mm:ss`. One still running by then is left behind, and its turn ends in a handoff. | `"00:00:10"` |

## `operations`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `telemetry` | section | `{"cacheHitWarning":0.7}` | Measurements and the warnings raised from them. Required. | `{"cacheHitWarning":0.7}` |
| `storage` | section | `{"path":".sof/sof.db"}` | Where the default local storage keeps its files, for a host that uses it, such as `sof`. A host that supplies its own storage in code has no use for it. Required. | `{"path":".sof/sof.db"}` |

## `storage`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `unstoredEvents` | list |  | Kinds of event that are published live but not stored, so a reader that joins late or falls behind does not see them. Unset leaves out streamed text only: storing each piece slows the agent, and the conversation keeps the text. An empty list in a file counts as unset; to store every kind, set an empty list in code. | `["textGenerated","modelCallEnded"]` |
| `retention` | section | `{"audit":"365.00:00:00"}` | How long each kind of stored data is kept. Data without a period is kept until it is deleted. | `{"events":"30.00:00:00"}` |

## `capabilities`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `conversationStore` | section | `{"enabled":false}` | The conversation store: each agent's conversation with each caller is kept, anonymous callers sharing one, so a history strategy other than `none` continues it across requests and restarts. | `{"enabled":true}` |
| `knowledge` | section | `{"enabled":false}` | Knowledge retrieval: the sources in `knowledge`, searched before a turn or through `knowledge:` tools. | `{"enabled":true}` |
| `humanInteraction` | section | `{"enabled":false}` | Human interaction: the `builtin:human.ask_owner` tool, and sign-offs where the run waits for the owner. | `{"enabled":true,"signOffs":["runBudgetExceeded"]}` |
| `workspace` | section | `{"enabled":false,"protectedPaths":[],"baselineChecks":[],"keepWorkingCopies":false}` | The git workspace: a working copy per agent, and an integration queue into the baseline. | `{"enabled":true,"protectedPaths":[{"path":"secrets/**","access":"hidden"}]}` |
| `sandbox` | section | `{"enabled":false,"allowedHosts":[],"toolchains":[],"commandRules":[],"secrets":{}}` | The sandbox that commands run in: no network unless allowed, and command rules. It needs the workspace. | `{"enabled":true,"allowedHosts":["api.nuget.org"]}` |
| `taskBoard` | section | `{"enabled":false,"maxAttempts":3,"budget":{"cost":8}}` | The task board: tasks with dependencies, verification checks and review, which agents change through the `tasks.*` tools and the owner at any time. | `{"enabled":true,"maxAttempts":2}` |
| `projectMemory` | section | `{"enabled":false,"scope":"project","maxTokens":20000,"approveBy":"lead"}` | Project memory: durable instructions, conventions and decisions in every agent's stable prefix, which agents change through the `memory.*` tools once the lead or the owner approves. | `{"enabled":true,"scope":"project","approveBy":"lead"}` |
| `checkpoints` | section | `{"enabled":false}` | Checkpoints: saved states a run resumes from after a crash and the owner can roll back to, with the working copies restored together with the run's state. | `{"enabled":true,"at":["turn","integration"]}` |
| `team` | section | `{"enabled":false,"helperDepth":2,"helperCount":4}` | The team: agents of several roles that work at once over the task board, led by one lead, with the `team.*` tools; agents use it through the `team` pattern. It needs the task board. | `{"enabled":true}` |

## `providers.<name>.apiKey`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `secret` | text |  | The name of the secret, such as the environment variable that holds it. Required. | `"ANTHROPIC_API_KEY"` |

## `providers.<name>.prices.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `input` | number, ≥ 0 | `0` | USD per million input tokens. | `4` |
| `output` | number, ≥ 0 | `0` | USD per million output tokens. | `20` |
| `cacheRead` | number, ≥ 0 | `0` | USD per million tokens read from the cache. | `0.2` |
| `cacheWrite5m` | number, ≥ 0 | `0` | USD per million tokens written to the cache for five minutes. | `5` |
| `cacheWrite1h` | number, ≥ 0 | `0` | USD per million tokens written to the cache for an hour. | `8` |

## `providers.<name>.retry`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `maxAttempts` | whole number, ≥ 1 | `5` | How many times a call is made in all, the first included, when it fails as transient or rate-limited. 1 means no retries. When the attempts are used up, the next fallback is tried. | `5` |
| `initialDelay` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"00:00:01"` | How long to wait before the first retry, as `hh:mm:ss`. Each retry waits twice as long as the one before, up to `maxDelay`. If the provider asks for a longer wait, that is used instead. | `"00:00:01"` |
| `maxDelay` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"00:01:00"` | The longest wait the progression reaches, as `hh:mm:ss`. A wait the provider asks for is not cut short by it. | `"00:01:00"` |

## `providers.<name>.features`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `structuredOutput` | boolean | `false` | Whether the model's output is constrained to the agent's `output.schema` natively (Claude's `output_config.format`); then the schema is not repeated in the instructions. The core checks the output against the schema either way. Claude adds `additionalProperties: false` to each object schema without it, and refuses the call for a keyword it does not take, such as `minLength` or `additionalProperties: true`. | `true` |
| `clearToolResults` | boolean | `false` | Whether the provider clears the results of old tool calls from what the model reads once the conversation grows long (Claude's `clear_tool_uses` context editing). The stored history keeps them. | `true` |
| `taskBudget` | whole number, ≥ 20000 |  | The tokens a turn may generate and read from tool results, told to the model so it paces its work (Claude's task budget, at least 20,000). It is advice to the model: the limits in `budget` are what stop a turn. Unset means none. | `200000` |
| `refusalFallback` | boolean | `false` | Whether a call the model's safety classifiers decline is served by the fallback model the provider recommends for the refusal's category (Claude's server-side `fallbacks`, on the Claude API). Each model is priced as it serves, so every model it may use needs a price; a `modelFallback` event names it. | `true` |

## `agents.<name>.pattern`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `type` | text | `"toolLoop"` | `toolLoop`: a turn that calls tools until a stop condition holds; `singleCall`: a turn with no tools; `workflow`, `router`, `fanOut`, `evaluateAndRevise`, `planAndExecute` or `team`; or `extension:<id>` for a pattern the application registers, which reads the settings here that it needs. Required. | `"router"` |
| `steps` | list | `[]` | workflow: the steps, run in order from the first. Each needs an `id`. | `[{"id":"draft"},{"id":"review","agent":"reviewer","input":["draft"]}]` |
| `next` | list | `[]` | workflow: where to go after a step completes. The first rule from that step whose `when` holds decides; with none, the next step follows. | `[{"from":"triage","when":{"field":"output.kind","is":"question"},"goto":"end"}]` |
| `classify` | section |  | router: the step that classifies the input. Its output must be structured. Unset: a turn of the agent itself. | `{"agent":"classifier"}` |
| `on` | text |  | router: the field of the classification that picks the route. fanOut with `combine: majority`: the field the branches vote on. | `"output.route"` |
| `routes` | named entries | `{}` | router: the step for each value of `on`, by value. It gets the router's input. | `{"bug":{"agent":"developer"},"docs":{"agent":"writer"}}` |
| `otherwise` | text |  | router: the route taken when no route has the value. Unset: the work is handed off, as no route for a value. | `"bug"` |
| `branches` | list | `[]` | fanOut: the steps that run in parallel on the input; with `over`, the one step that runs on each item. | `[{"agent":"reviewer"},{"agent":"tester"}]` |
| `over` | text |  | fanOut: a list in the input, which must then be JSON, such as `input.files`. The branch runs once for each item. | `"input.files"` |
| `maxParallel` | whole number, ≥ 1 | `4` | fanOut: the most branches that run at once. team: the most agents, the lead included, that work at once. | `2` |
| `combine` | `"all"`, `"firstSuccess"`, `"majority"`, `"step"` | `"all"` | fanOut: how the branches' results are combined. `all`: every branch must complete, and the output is the JSON list of their outputs; `firstSuccess`: the first to complete, and the others are stopped; `majority`: the output of a branch whose value of `on` more than half of the branches share; `step`: `combiner` combines their outputs. | `"majority"` |
| `combiner` | section |  | fanOut with `combine: step`: the step that gets the branches' outputs and combines them. | `{"agent":"editor"}` |
| `generate` | section |  | evaluateAndRevise: the step that produces the work. Unset: a turn of the agent itself. | `{"agent":"developer"}` |
| `checks` | list | `[]` | evaluateAndRevise: the checks, by name in `checks`, that the work must pass, in order. The first failure's findings go back to `generate`, masked, with the input and the work. | `["build","tests"]` |
| `maxRevisions` | whole number, ≥ 0 | `3` | evaluateAndRevise: how many times the work is revised before it is handed off, as an output check failure. | `5` |
| `planner` | section |  | planAndExecute: the step that writes the plan: structured output with a `steps` list. Unset: a turn of the agent itself. | `{"agent":"planner"}` |
| `executor` | section |  | planAndExecute: the step that carries out each item of the plan's `steps`, in order. | `{"agent":"developer"}` |
| `maxReplans` | whole number, ≥ 0 | `2` | planAndExecute: how many times the planner is asked for a new plan after a step does not complete, before the work is handed off. | `1` |
| `lead` | text |  | team: the agent, by name in `agents`, that plans the work as tasks on the board, decides on the tasks that fail, and reports. It works in turns of its own. | `"lead"` |
| `roles` | named entries | `{}` | team: the agents, by name in `agents`, that do and review the tasks, with how many of each work at once. Each is an agent of its own, such as `developer[2]`, which works in turns of its own and keeps no history. | `{"developer":{"max":3},"reviewer":{"max":1}}` |

## `agents.<name>.context`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `operatingFacts` | list | `[]` | Facts added after the history for every model call, in this order, such as limits or the date. Placeholders may use `{{now}}` and `{{now:date}}`, and the project and agent values. | `["Today is {{now:date}}.","Replies are limited to 300 words."]` |
| `historyCacheLifetime` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"00:05:00"` | How long the provider keeps the conversation cached between model calls, as `hh:mm:ss`, at most one hour. Agents that often wait for approvals benefit from longer. | `"01:00:00"` |
| `record` | list |  | The kinds of run record entry the agent sees in its model input: `fact`, `finding`, `decision` and `citation`; unset for `fact`, `finding` and `decision`; list `citation` to show citations too. Facts come before retrieved knowledge, and the rest after it. | `["fact","decision"]` |
| `recordScope` | `"all"`, `"task"` | `"all"` | `all`: the agent sees the run record's entries of the kinds in `record`. `task`: only those made for the task it works on. | `"task"` |
| `currentTask` | boolean | `true` | Whether the volatile context shows the status and acceptance criteria of the task the agent works on. | `false` |
| `retrieval` | section | `{"beforeTurn":[],"handOffWhenNotCovered":false}` | Knowledge retrieved before each turn. Required. | `{"beforeTurn":["handbook"],"handOffWhenNotCovered":true}` |
| `history` | section | `{"strategy":"none","shortening":"provider","lastTurns":10}` | What history a request starts with, and how it is shortened. Required. | `{"strategy":"shortened"}` |

## `agents.<name>.output`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `format` | `"text"`, `"structured"` | `"text"` | `text`, or `structured`: JSON that must match `schema`. | `"structured"` |
| `schema` | text |  | The JSON Schema that structured output must match, as JSON text. The output is checked against it after every reply. Unless the provider constrains output to it (`features.structuredOutput`), the model is told it after the instructions: describe the shape briefly in the instructions too. | `"{ \u0022type\u0022: \u0022object\u0022, \u0022required\u0022: [\u0022total\u0022] }"` |
| `attempts` | whole number, ≥ 0 | `2` | How many times output goes back to the model with its problems before the turn is handed off: structured output that does not match the schema and, with `onCheckFailure: revise`, output that fails a check. | `3` |
| `checks` | list | `[]` | Checks, by name in `checks`, that the output must pass, run in this order. The first that fails decides. | `["no-secrets","style"]` |
| `onCheckFailure` | `"handoff"`, `"revise"` | `"handoff"` | What a failed check, or citation rule, does: `handoff` the turn, or `revise`: its findings, masked, go back to the model, which replies again, within `attempts`. | `"revise"` |
| `citations` | `"off"`, `"resolve"`, `"required"` |  | `off`; `resolve`: every id the output cites as `[cite:<id>]` must be a citation in the run record; or `required`: as `resolve`, and the output must cite at least one. Output that fails hands the turn off. Unset means `resolve` when the knowledge capability is on, otherwise `off`. | `"required"` |

## `agents.<name>.stopWhen`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `finished` | boolean | `true` | The turn completes when the model finishes its reply. When this is `false` and the model finishes, the turn ends in a handoff. | `false` |
| `finishTool` | text |  | A tool, by name in `tools`, that completes the turn when a call of it succeeds. The call's arguments are the output. | `"submit_report"` |
| `checksPass` | boolean | `false` | The turn completes as soon as its output passes the checks in `output.checks`, which then run after every model reply. A reply that only calls tools is checked with empty output. | `true` |
| `maxIterations` | whole number, ≥ 1 |  | The turn completes after this many model calls, with the model's last text as the output. | `1` |

## `agents.<name>.budget`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `turn` | section | `{"iterations":50,"toolCalls":200,"tokens":3000000,"cost":5,"time":"00:45:00"}` | The limits of each turn, checked before every model call. A turn that reaches one ends in a handoff. Required. | `{"iterations":50,"cost":5}` |
| `total` | section |  | The limits of all the agent's turns in a run together, checked before every model call. Unset means the agent has no limit of its own beyond its turns' and the run's. An agent that reaches one ends its turn in a handoff. | `{"cost":10,"toolCalls":500}` |

## `agents.<name>.stall`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `iterationsWithoutProgress` | whole number, ≥ 1 | `3` | A turn ends in a handoff after this many tool-calling iterations in a row that only repeat earlier calls and get the same results. | `5` |

## `tools.<name>.limits`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `maxUses` | whole number, ≥ 1 |  | The most calls of the tool in one model call. Unset leaves it to the provider. | `5` |
| `allowedDomains` | list | `[]` | The only domains the tool may reach, such as for a web search or fetch. Empty allows any. | `["learn.microsoft.com","github.com"]` |

## `gates.<name>.when`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `field` | text |  | The field a test reads: a dotted path with optional `[n]` indexes, such as `args.branch` in a tool's arguments or `output.kind` in a step's output. | `"args.branch"` |
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

## `policies.masking`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `enabled` | boolean | `true` | Whether masking is on. Turn it off where it would corrupt the content, such as source code. | `false` |
| `patterns` | named entries |  | What is masked: regular expressions by name, which replace the built-in ones for email addresses, phone numbers and payment card numbers. Each match becomes a token such as `[email-1]`, the same for the same value throughout the run. Names may use letters, digits and underscores. | `{"email":"[^@\\s]\u002B@[^@\\s]\u002B","employeeId":"EMP-[0-9]{6}"}` |

## `policies.rateLimits`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `perOwner` | section |  | The limit for each owner. Anonymous callers share one. No limit when unset. | `{"permits":20,"window":"01:00:00"}` |
| `perTenant` | section |  | The limit for each tenant. Callers without a tenant share one. No limit when unset. | `{"permits":500,"window":"01:00:00"}` |
| `perRun` | section |  | The limit for each run: how many work items may join one run, such as a run that resumes or a team's tasks. A new run's first work item counts as one. No limit when unset. | `{"permits":100,"window":"01:00:00"}` |

## `run.budget`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `cost` | number, > 0 | `25` | The most the run may spend, in USD. Live: the owner may change it during a run. | `25` |
| `time` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"08:00:00"` | The longest the run may take, as `hh:mm:ss` or `d.hh:mm:ss`. Time in which an agent of it waits for the owner, to answer or to resume it, does not count. Live: the owner may change it during a run. | `"08:00:00"` |
| `tokens` | whole number, ≥ 1 |  | The most tokens the run may use: input, output, cache reads and cache writes together. Unset: only cost and time limit the run. Live: the owner may change it during a run. | `500000000` |
| `toolCalls` | whole number, ≥ 1 |  | The most tool calls the run may make, including tools the provider runs itself. Unset: only cost and time limit the run. Live: the owner may change it during a run. | `20000` |

## `operations.telemetry`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `cacheHitWarning` | number, ≥ 0, ≤ 1 | `0.7` | The share of a model call's input read from the provider's cache, from 0 to 1, below which a warning is raised. The first call of a turn is not checked. | `0.7` |

## `operations.storage`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `path` | text | `".sof/sof.db"` | The database file (SQLite); the artifacts' files are in the folder `artifacts` beside it. A relative path is relative to the project directory and must stay in `.sof/`, which agents cannot see. An absolute path must lead into the project's `.sof/` or out of the project. Required. | `".sof/sof.db"` |

## `storage.retention`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `runs` | time span (`hh:mm:ss` or `d.hh:mm:ss`) |  | How long a run, with the configuration it used, is kept after it starts, as `d.hh:mm:ss`. | `"90.00:00:00"` |
| `events` | time span (`hh:mm:ss` or `d.hh:mm:ss`) |  | How long an event is kept after it happens, as `d.hh:mm:ss`. | `"30.00:00:00"` |
| `conversations` | time span (`hh:mm:ss` or `d.hh:mm:ss`) |  | How long a turn of a stored conversation is kept after it ends, as `d.hh:mm:ss`. Once its earliest turns are deleted, a conversation continues from the turns kept. | `"90.00:00:00"` |
| `runRecords` | time span (`hh:mm:ss` or `d.hh:mm:ss`) |  | How long a run's record is kept after its last change, as `d.hh:mm:ss`. It is kept or deleted whole, never trimmed. | `"365.00:00:00"` |
| `taskBoards` | time span (`hh:mm:ss` or `d.hh:mm:ss`) |  | How long a run's task board, with each task's history, is kept after its last change, as `d.hh:mm:ss`. It is kept or deleted whole. | `"365.00:00:00"` |
| `artifacts` | time span (`hh:mm:ss` or `d.hh:mm:ss`) |  | How long an artifact, such as the full text of a trimmed tool result, is kept after it is made, as `d.hh:mm:ss`. | `"90.00:00:00"` |
| `audit` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"365.00:00:00"` | How long an audit entry is kept after it is written, as `d.hh:mm:ss`. A request to delete an owner's data leaves audit entries to this period. | `"730.00:00:00"` |

## `capabilities.conversationStore`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `enabled` | boolean | `false` | Whether the capability is on. | `true` |

## `capabilities.humanInteraction`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `enabled` | boolean | `false` | Whether human interaction is on. | `true` |
| `signOffs` | list |  | Where a run waits for the owner's sign-off: `runBudgetExceeded`, to go on past the run's budget, which otherwise ends the turn; `irreversibleAction`, before every irreversible tool call, whatever the tool's `approval`; and `planApproval`, before a team's work starts on its lead's plan. Unset means the first two. An empty list in a file counts as unset; to turn both off, set an empty list in code. | `["runBudgetExceeded"]` |

## `capabilities.workspace`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `enabled` | boolean | `false` | Whether the workspace is on. | `true` |
| `protectedPaths` | list | `[]` | Paths agents cannot see or change, in addition to the fixed ones: `.git`, `**/.env*` and `.sof/**` are hidden, and `sof.json` and `sof.*.json` are read-only. | `[{"path":"secrets/**","access":"hidden"}]` |
| `baselineChecks` | list | `[]` | The checks, by name in `checks`, that the baseline must still pass with a change before it is integrated, in order. They run on the change applied to the baseline as it is when its turn in the integration queue comes. | `["build","tests"]` |
| `keepWorkingCopies` | boolean | `false` | Whether an agent's working copy is kept when its task ends, so the owner can look at it. | `true` |

## `capabilities.sandbox`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `enabled` | boolean | `false` | Whether the sandbox is on. | `true` |
| `allowedHosts` | list | `[]` | The hosts commands may reach, through a filtering proxy. `*.` matches any subdomain. Empty means no network. | `["api.nuget.org","*.nuget.org"]` |
| `toolchains` | list | `[]` | Folders of toolchains installed outside the system folders, such as an SDK in the home folder. Commands can read and run them, and find them on the path. | `["/home/dev/.dotnet"]` |
| `commandRules` | list | `[]` | Rules for commands, in order. Each command of a command line is decided by the first rule that matches it, and the strictest decision applies. A command no rule matches is asked about. | `[{"match":"dotnet build*","action":"allow"},{"match":"git push*","action":"deny"}]` |
| `secrets` | named entries | `{}` | The secrets each agent's commands receive as environment variables, by agent name. Other agents' commands never see them. | `{"developer":["NUGET_TOKEN"]}` |

## `capabilities.taskBoard`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `enabled` | boolean | `false` | Whether the task board is on. | `true` |
| `maxAttempts` | whole number, ≥ 1 | `3` | How many attempts of a task may fail a check, a review or an integration before it goes back to the lead. | `3` |
| `budget` | section | `{"cost":8}` | What a task's turns may spend together. A task whose budget is used up goes back to the lead. | `{"cost":8,"tokens":20000000,"toolCalls":500,"time":"02:00:00"}` |

## `capabilities.projectMemory`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `enabled` | boolean | `false` | Whether project memory is on: every agent's stable prefix then holds the memory, and agents propose changes with the `memory.*` tools. | `true` |
| `scope` | `"project"`, `"owner"`, `"tenant"` | `"project"` | Whose memory it is: `project` (one per project name), `owner` (one per caller) or `tenant` (one per tenant). Agents of one definition share a prefix only within one scope. | `"project"` |
| `maxTokens` | whole number, ≥ 1 | `20000` | The size limit, in tokens, counted as one token per four characters. A change that would take memory past it is not applied: agents propose a condensed version instead, which only the owner approves. | `20000` |
| `approveBy` | `"lead"`, `"owner"` | `"lead"` | Who approves an agent's proposed change: `lead`, the lead of a team, through a `builtin:memory.review` tool, which no other agent can use (outside a team, proposals wait for the owner), or `owner`, who is asked at the proposal and needs `humanInteraction`. Both memory tools are write tools, so each needs `gates` or a `gateExemption`, and under `permissionMode: ask` each call also asks the owner unless a permission rule allows it. | `"lead"` |

## `capabilities.checkpoints`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `enabled` | boolean | `false` | Whether checkpoints are on. A run then takes one when it starts, and one at each point in `at`; the owner can take one at any time. A crashed run resumes from its last, and the owner can roll a run back to any. It needs the conversation store. | `true` |
| `at` | list |  | Where a checkpoint is taken: `turn` after each turn, `step` after each step of a pattern, `integration` after each integration into the baseline. Unset means `turn`. An empty list in a file counts as unset. | `["turn","integration"]` |

## `capabilities.team`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `enabled` | boolean | `false` | Whether the team is on. | `true` |
| `helperDepth` | whole number, ≥ 1 | `2` | How deep helpers may go: a helper of a helper is depth 2. | `1` |
| `helperCount` | whole number, ≥ 1 | `4` | The most helpers one turn of an agent may start. | `2` |

## `agents.<name>.pattern.steps[]`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `id` | text |  | workflow: the step's name, which `input`, `next` and `goto:` use. It cannot be `input` or `end`. | `"triage"` |
| `agent` | text |  | The agent, by name in `agents`, whose work the step is: its turn, or its own pattern. Unset, with no `pattern`: a turn of the agent the pattern belongs to. | `"reviewer"` |
| `pattern` | section |  | A nested pattern, of the agent the pattern belongs to. | `{"type":"evaluateAndRevise","checks":["tests"]}` |
| `input` | list |  | workflow: what the step gets: `input` for the workflow's input, or an earlier step's id for its output. Several are each labelled with their source. Unset: the workflow's input. | `["input","triage"]` |
| `onOutcome` | section | `{"completed":"continue","handedOff":"handoff","failed":"handoff"}` | workflow: what happens after each outcome of the step. Required. | `{"failed":"retry:1","handedOff":"goto:escalate"}` |

## `agents.<name>.pattern.next[]`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `from` | text |  | The step, by id, after which the rule applies. Required. | `"triage"` |
| `when` | section |  | The condition on the step's structured output, such as `output.kind`. Unset means always. | `{"field":"output.kind","is":"question"}` |
| `goto` | text |  | The step, by id, to go to, or `end`. Required. | `"end"` |

## `agents.<name>.pattern.roles.<name>`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `max` | whole number, ≥ 1 | `1` | How many of the agent can work at once. | `3` |

## `agents.<name>.context.retrieval`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `beforeTurn` | list | `[]` | Knowledge sources, by name in `knowledge`, searched with the turn's work before the turn starts. Their passages are given to the model as data in the volatile context. | `["handbook"]` |
| `handOffWhenNotCovered` | boolean | `false` | Whether the turn ends in a handoff for a policy gap, without calling the model, when no source searched before the turn covers the work. | `true` |

## `agents.<name>.context.history`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `strategy` | `"none"`, `"full"`, `"shortened"`, `"lastTurns"` | `"none"` | `none`: each request starts a new conversation. `full`: the agent's conversation with the caller continues, and is never shortened, so once it is too long for the model the turn is handed off. `shortened`: it continues, and is shortened once when the model reports it too long. `lastTurns`: it continues with the last `lastTurns` turns only. All but `none` need the conversation store. | `"shortened"` |
| `shortening` | text | `"provider"` | How `shortened` history is shortened: `provider` by the model provider's own mechanism, or `extension:<id>` by a shortener the application registers. The current turn is never shortened. Required. | `"extension:Acme.Summarizer"` |
| `lastTurns` | whole number, ≥ 1 | `10` | For `lastTurns`: how many earlier turns a request starts with. | `5` |

## `agents.<name>.budget.turn`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `iterations` | whole number, ≥ 1 | `50` | The most model calls in a turn. | `50` |
| `toolCalls` | whole number, ≥ 1 | `200` | The most tool calls in a turn, including tools the provider runs itself. | `200` |
| `tokens` | whole number, ≥ 1 | `3000000` | The most tokens a turn may use: input, output, cache reads and cache writes together. | `3000000` |
| `cost` | number, > 0 | `5` | The most a turn may spend, in USD. | `5` |
| `time` | time span (`hh:mm:ss` or `d.hh:mm:ss`) | `"00:45:00"` | The longest a turn may take, as `hh:mm:ss`. Time waiting for the owner, to answer or to resume it, does not count. | `"00:45:00"` |

## `agents.<name>.budget.total`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `toolCalls` | whole number, ≥ 1 |  | The most tool calls the agent's turns may make, including tools the provider runs itself. | `10000` |
| `tokens` | whole number, ≥ 1 |  | The most tokens the agent's turns may use: input, output, cache reads and cache writes together. | `100000000` |
| `cost` | number, > 0 |  | The most the agent's turns may spend, in USD. | `25` |

## `policies.rateLimits.perOwner`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `permits` | whole number, ≥ 1 |  | How many work items are admitted in each window. Required. | `20` |
| `window` | time span (`hh:mm:ss` or `d.hh:mm:ss`) |  | How long a window lasts, as `hh:mm:ss`. Required. | `"01:00:00"` |

## `capabilities.workspace.protectedPaths[]`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `path` | text |  | A glob relative to the workspace root, where `**` matches any number of folders. Use `dir/**` to protect a folder's contents: `dir` alone matches only the folder's own path. Required. | `"secrets/**"` |
| `access` | `"hidden"`, `"readOnly"` | `"hidden"` | `hidden`: agents cannot see it at all; `readOnly`: they can read it but not change it. | `"readOnly"` |

## `capabilities.sandbox.commandRules[]`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `match` | text |  | The command it applies to, where `*` matches any text and `?` any one character. Required. | `"dotnet test*"` |
| `action` | `"allow"`, `"ask"`, `"deny"` | `"deny"` | What a match decides: `allow`, `ask` a human, or `deny`. | `"allow"` |

## `capabilities.taskBoard.budget`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `cost` | number, > 0 | `8` | The most a task's turns may cost, in USD, unless the owner gives the task another budget. | `8` |
| `tokens` | whole number, ≥ 1 |  | The most tokens a task's turns may use: input, output, cache reads and cache writes together. Unset means no limit of its own. | `20000000` |
| `toolCalls` | whole number, ≥ 1 |  | The most tool calls a task's turns may make. Unset means no limit of its own. | `500` |
| `time` | time span (`hh:mm:ss` or `d.hh:mm:ss`) |  | The longest a task's turns may take together, as `hh:mm:ss`, less the time they wait for the owner. Unset means no limit of its own. | `"02:00:00"` |

## `agents.<name>.pattern.steps[].onOutcome`

| Setting | Allowed values | Default | Description | Example |
|---|---|---|---|---|
| `completed` | text | `"continue"` | After the step completes: `continue` to the step `next` picks, `retry:<n>`, `goto:<step>` or `handoff` to end the workflow with the step's result. | `"goto:publish"` |
| `handedOff` | text | `"handoff"` | After the step is handed off: `continue`, `retry:<n>`, `goto:<step>` or `handoff`. | `"retry:1"` |
| `failed` | text | `"handoff"` | After the step fails: `continue`, `retry:<n>`, `goto:<step>` or `handoff`. | `"retry:2"` |
