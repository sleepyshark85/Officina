# Officina — Configuration

Status: draft for M0 design review · 2026-09-30 · companion to `REQUIREMENTS.md` (revision 2)

## How it works

- **Every setting has a default in code.** Each section of the configuration is a plain C# Options
  class (a record with initialized properties) in `Sleepyshark.Officina.Core`. Those initializers are the defaults
  (CFG-16).
- **Settings exist only when needed.** A value becomes a setting when there is a known case that needs
  a different value. Until then it is a constant in code. Adding a setting later is cheap; removing one
  is not.
- **A configuration file only holds what differs.** `sof.json` is merged on top of the code
  defaults. It is JSON, and comments are allowed.
- **Presets are ready-made files** that sit between the code defaults and your file. For example,
  `preset:coding-team` will set up the whole team; presets arrive with the coding team.
- **Layers, lowest to highest:** code defaults < `sof.json` < `sof.<environment>.json`
  < environment variables (`SOF__section__setting`) < CLI options. Loading uses
  Microsoft.Extensions.Configuration, so setting names match ignoring case, unknown settings are ignored,
  a list should be set in one layer only (lists merge by position), and `null` only unsets a setting that
  may be unset. Agent definitions cannot build on each other yet.
- **To see what is in effect,** run `sof config show --origin`. It lists every setting with its effective
  value and the file, variable or option it came from, down to "code default, core 1.2.0" (CFG-04).
- **Everything is validated before anything runs** (CFG-06). `sof config validate` checks a
  file without running it. A value of the wrong type is reported one at a time.

The full list of settings is in [`docs/configuration-reference.md`](docs/configuration-reference.md).
The settings implemented so far are in [`docs/configuration-settings.md`](docs/configuration-settings.md),
generated from the Options classes with the JSON Schema [`docs/officina.schema.json`](docs/officina.schema.json)
(CFG-15, DOC-01).

## What you must specify (CFG-17)

| Situation | Required | Why there is no default |
|---|---|---|
| Every agent | `instructions` | Only the application knows the job |
| Every write tool | `gates`, or a `gateExemption` with a reason | INV-04 forbids a default |
| Every application tool, gate, check or knowledge source | its `source` and any settings it declares as required | It is the application's own code |
| Coding team | `project.values.buildCommand` and `testCommand` | Project-specific. `sof init` detects them, so usually you only confirm them. |

Everything else has a default. The API key is read from the `ANTHROPIC_API_KEY` environment
variable unless you configure another secret.

Some Claude models (`claude-haiku-4-5`, the Sonnet 5 models) take no system message in the middle of a conversation. The provider knows which, so the volatile context goes into the history for them, but a message from an operator sent during a turn is still a system message, and such a model rejects it with `InvalidRequest`.

## Smallest configurations

One agent, which runs on the default model profile (`claude-opus-5-5`):

```json
{
  "agents": {
    "extractor": { "instructions": "Extract the invoice number, date and total. Reply as JSON." }
  }
}
```

The coding team:

Presets arrive with the coding team; until then this is how the team will be set up:

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
| `run.permissionMode` | `ask` | You trust the rules enough for `auto`, or want `readOnly` |
| `capabilities.sandbox.allowedHosts` | none (network off) | Builds download packages |
| `capabilities.sandbox.commandRules` | the preset's rules; anything unmatched is asked about | You want fewer or more prompts. An allow rule for an interpreter, such as `sh*` or `bash*`, allows every command. |
| `capabilities.workspace.protectedPaths` | `.git`, `.env*` and `.sof/` hidden; `sof.json` and `sof.*.json` read-only | More files must stay out of reach |
| `capabilities.humanInteraction.signOffs` | plan approval, run budget exceeded, irreversible action | You want more or fewer checkpoints with the owner |
| `capabilities.team.maxParallelAgents` | 4 | Your machine or budget allows more or fewer |

## Defaults are the safe choice

Every default is the safe option:
- network off;
- permission mode `ask`;
- irreversible tools need approval;
- no helper agents;
- masking on (the coding preset turns it off, because it would corrupt source code);
- conversation content not logged.

Invariants (INV-01…10) are not settings at all, so no file can weaken them.

When a core release changes a default, the release notes say so. Each run stores its fully
resolved configuration (CFG-07), so earlier runs remain reproducible.
