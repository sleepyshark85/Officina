# Officina — Configuration

Companion to `REQUIREMENTS.md` (revision 2). Describes what is built in v1.

## How it works

- **Every setting has a default in code.** Each section of the configuration is a plain C# Options
  class (a record with initialized properties) in `Sleepyshark.Officina.Core`. Those initializers are the defaults
  (CFG-16).
- **Settings exist only when needed.** A value becomes a setting when there is a known case that needs
  a different value. Until then it is a constant in code. Adding a setting later is cheap; removing one
  is not.
- **A configuration file only holds what differs.** `sof.json` is merged on top of the code
  defaults. It is JSON, and comments are allowed.
- **Presets are ready-made files** that sit between the code defaults and your file. A file names them,
  and other files of its own, in `extends`: `"extends": ["preset:coding-team"]` sets up the whole team. Three
  presets ship: `coding-team`, `tool-using-assistant` and `single-call-extractor`.
- **Layers, lowest to highest:** code defaults < what `sof.json` extends < `sof.json` < `sof.<environment>.json`
  < environment variables (`SOF__section__setting`) < CLI options. Files merge object by object, setting names
  match ignoring case, and a list or a value in a higher file replaces the lower one's. Environment variables
  and CLI options are bound by Microsoft.Extensions.Configuration, so a list there merges by position. Unknown
  settings are ignored, and `null` only unsets a setting that may be unset.
- **An agent definition can build on another** with `"extends": "<agent>"`: it inherits every setting it does
  not set itself, with the same rule for lists. The coding team's roles share a base this way (CFG-05).
- **To see what is in effect,** run `sof config show --origin`. It lists every setting with its effective
  value and the file, variable or option it came from, down to "code default, core 0.1.0" (CFG-04).
- **Everything is validated before anything runs** (CFG-06). `sof config validate` checks the
  configuration without running it. A value of the wrong type is reported one at a time.

How the settings fit together (patterns, conditions, merging, validation, presets) is in the
[configuration reference](docs/configuration-reference.md). Every setting, with its default and an example, is in the
[settings reference](docs/configuration-settings.md), generated from the Options classes with the JSON Schema
[`docs/officina.schema.json`](docs/officina.schema.json) (CFG-15, DOC-01).

## What you must specify (CFG-17)

| Situation | Required | Why there is no default |
|---|---|---|
| Every agent | `instructions` | Only the application knows the job |
| Every write tool | `gates`, or a `gateExemption` with a reason | INV-04 forbids a default |
| Every application tool, gate, check or knowledge source | its `source` and any settings it declares as required | It is the application's own code |
| Coding team | `project.values.buildCommand` and `testCommand` | Project-specific. `sof init` detects them where it can |

Everything else has a default. The API key is read from the `ANTHROPIC_API_KEY` environment
variable unless you configure another secret.

Only some Claude models (Opus 5 and 4.8, Fable 5, Mythos 5 and Sonnet 5.5) take a system message in the middle of a
conversation. For the others, such as `claude-haiku-4-5` and Sonnet 5, the provider appends the volatile context to the
history instead, and sends operator messages and memory changes as user messages that start `Message from the operator:`.
No setting is needed.

## Smallest configurations

One agent, which runs on the default model profile (`claude-opus-5-5`):

```json
{
  "agents": {
    "extractor": { "instructions": "Extract the invoice number, date and total. Reply as JSON." }
  }
}
```

The coding team, which `sof init` writes with the commands it detects, a low `run.budget` and what the commands need in
the sandbox (see the [user guide](docs/user-guide.md), section 6):

```jsonc
{
  "extends": ["preset:coding-team"],
  "project": { "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" } }
}
```

## Settings people commonly change

| Setting | Default | Change it when |
|---|---|---|
| `agents.<name>.model` | `claude-opus-5-5` at the provider's default effort | A role needs a different model or effort |
| `models.<name>` | one profile, `default` | You want named profiles, or fallbacks for when a model is unavailable |
| `agents.<name>.tools` | no tools | The agent should act |
| `tools.<name>.approval` | `always` for irreversible tools, otherwise `never` | A tool needs human sign-off |
| `toolServers` | none | You use MCP tool servers |
| `run.budget` | $25 and 8 hours (`"08:00:00"`) | Runs are bigger or smaller |
| `agents.<name>.budget.total` | none | One agent should spend less of the run than the rest |
| `run.permissionMode` | `ask` | You trust the rules enough for `auto`, or want `readOnly` |
| `capabilities.sandbox.allowedHosts` | none (network off) | Builds download packages |
| `capabilities.sandbox.toolchains` | none: on Linux, commands find programs only in `/usr/local/bin`, `/usr/bin` and `/bin` | An SDK is elsewhere, such as `~/.dotnet` or `/usr/lib/dotnet`; `sof init` names the folder |
| `capabilities.sandbox.commandRules` | the preset's rules; anything unmatched is asked about | You want fewer or more prompts. An allow rule for an interpreter, such as `sh*` or `bash*`, allows every command. |
| `capabilities.workspace.protectedPaths` | `.git`, `.env*` and `.sof/` hidden; `sof.json` and `sof.*.json` read-only | More files must stay out of reach |
| `capabilities.humanInteraction.signOffs` | run budget exceeded and irreversible action; the coding team adds plan approval | You want more or fewer checkpoints with the owner |
| `capabilities.checkpoints.at` | after each turn | You want one after each step of a pattern or each integration, as well |
| `operations.storage` | `.sof/sof.db`, with artifacts as files in `.sof/artifacts` | The storage belongs elsewhere: another folder in `.sof/`, or an absolute path outside the project. The artifacts stay in `artifacts` beside the database |
| `agents.<team>.pattern.maxParallel` | 4, the lead included | Your machine or budget allows more or fewer agents at once |

## Defaults are the safe choice

Every default is the safe option:
- network off;
- permission mode `ask`;
- irreversible tools need approval;
- no helper agents (an agent starts helpers only when its definition lists them);
- masking on (the coding preset turns it off, because it would corrupt source code);
- conversation content not logged.

Invariants (INV-01…10) are not settings at all, so no file can weaken them.

When a core release changes a default, the release notes say so. Each run stores its fully
resolved configuration (CFG-07), so earlier runs remain reproducible.
