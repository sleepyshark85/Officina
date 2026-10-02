# sof user guide

How to set up `sof`, the Officina coding team CLI, and use it by hand. For every setting, see
[`CONFIGURATION.md`](../CONFIGURATION.md) and the [settings reference](configuration-settings.md).

> **Status.** Every slice passes its tests on Linux and Windows, but the tests use a scripted model. No full live run
> against Claude has been done yet. Treat your first sessions as a field test: start small and keep the budget low.

## 1. Prerequisites

| Need | Linux | Windows |
|---|---|---|
| .NET SDK | 10.0.100 or later | same |
| git | on PATH | same |
| Sandbox (the coding team turns it on) | `bubblewrap`, `socat`, cgroups v2, a systemd user manager | Windows 10/11 or Server; no admin rights needed |
| Claude API key | `ANTHROPIC_API_KEY` in the environment | same |
| Python 3 | only for the benchmark | same |

On Ubuntu 23.10 and later, run this once as root. It is the same setup CI uses:

```bash
sudo apt-get install -y bubblewrap socat
printf 'abi <abi/4.0>,\ninclude <tunables/global>\nprofile bwrap /usr/bin/bwrap flags=(unconfined) {\n  userns,\n}\n' \
  | sudo tee /etc/apparmor.d/bwrap
sudo apparmor_parser -r /etc/apparmor.d/bwrap
sudo loginctl enable-linger "$USER"   # a systemd user manager for the cgroup limits
```

Don't turn off `kernel.apparmor_restrict_unprivileged_userns` instead; that weakens the whole machine.

## 2. Build and install

```bash
git clone https://github.com/sleepyshark85/Officina.git
cd Officina
dotnet build
dotnet test          # offline, no API key needed
dotnet pack src/Sleepyshark.Officina.Cli -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg Sleepyshark.Officina.Cli
sof --help
```

To update later, `dotnet pack` again and run `dotnet tool update --global --add-source ./nupkg Sleepyshark.Officina.Cli`.
Without installing, `dotnet run --project src/Sleepyshark.Officina.Cli -- <args>` works the same way.

## 3. Your first agent (no tools, no sandbox)

In an empty folder, create `sof.json`:

```json
{
  "agents": {
    "assistant": { "instructions": "Answer in three sentences or fewer." }
  },
  "run": { "budget": { "cost": 0.5 } }
}
```

Then:

```bash
sof config validate                     # exits 1 and lists the errors if any
sof config show --origin                # every setting, its value and where it came from
sof config dry-run --input "Hi" --reply "Hello."   # prints the configuration, then runs a scripted model: no API call
export ANTHROPIC_API_KEY=...            # Windows: $env:ANTHROPIC_API_KEY = "..."
sof run --input "What is a git worktree?"
```

`sof run` prints the run id first, streams what the agent does, and ends with the run report. The run is stored in
`.sof/sof.db` next to `sof.json`.

## 4. Configuration essentials

- **Layers, lowest to highest:** code defaults < files named in `extends` (presets first) < `sof.json` <
  `sof.<environment>.json` < `SOF__section__setting` variables < command-line options. A list in a higher layer replaces
  the lower one's whole.
- **Presets:** `preset:coding-team`, `preset:tool-using-assistant` and `preset:single-call-extractor`. Read them in
  [`src/Sleepyshark.Officina.Core/Presets/`](../src/Sleepyshark.Officina.Core/Presets/).
- **Models:** the default profile is `claude-opus-5-5`. Name more in `models`, and point an agent at one with
  `agents.<name>.model`. Haiku 4.5 and Sonnet 5 take no system message mid-conversation, so avoid them for agents that
  get operator messages or use project memory.
- **Budgets:** `run.budget` defaults to $25 and 8 hours. Set it low while testing: `--budget 2` on the command line
  overrides it for one run.
- **Write tools** need `gates` or a `gateExemption` with a reason, or validation fails.
- **Permission mode:** `ask` (default; you approve writes), `auto` or `readOnly`. Set it with
  `--permission-mode` or switch it during a run with `mode <name>`.
- **Command options shared by every command:** `--dir <folder with sof.json>`, `--environment <name>`, `--budget <usd>`,
  `--permission-mode <mode>`.

## 5. Running and the console

```
sof run [--agent <name>] --input "<the work>"
```

While it runs, type these at the console. Numbers come from `status`.

| Command | What it does |
|---|---|
| `status` | Each agent's status, what waits for you (numbered), cost so far |
| `approve <n>` / `deny <n>` | Answer an approval or a sign-off |
| `change <n> <json>` | Approve a tool call with changed arguments |
| `answer <n> <text>` | Answer a question an agent asked you |
| `tell <agent> <text>` | Send an agent a message, such as `tell lead focus on the parser` |
| `mode <ask\|auto\|readOnly>` | Change the permission mode |
| `pause [agent]` / `resume [agent]` | Pause or resume the whole run, or one agent such as `developer[2]` |
| `cancel [agent]` | Stop one agent, or the run |
| `checkpoint` | Take a checkpoint now |
| `board` | Show the task board |
| `memory`, `memory approve <n> [reason]`, `memory reject <n> <reason>` | Review project memory proposals |

Ctrl+C cancels the run. Exit codes: 0 done, 1 configuration errors, 2 usage error, 3 the run ended without completing
(handed off, rejected or failed).

## 6. The coding team

Run it inside a git repository, with `sof.json` in the repository's top folder. A minimal configuration for a .NET project:

```jsonc
{
  "extends": ["preset:coding-team"],
  "project": { "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" } },
  "run": { "budget": { "cost": 5 } },
  "capabilities": {
    "sandbox": {
      "allowedHosts": ["api.nuget.org", "*.nuget.org"],
      // This list replaces the preset's, so its rules are repeated.
      "commandRules": [
        { "match": "git status*", "action": "allow" }, { "match": "git diff*", "action": "allow" },
        { "match": "git log*", "action": "allow" },    { "match": "git show*", "action": "allow" },
        { "match": "git push*", "action": "deny" },    { "match": "git remote*", "action": "deny" },
        { "match": "dotnet *", "action": "allow" }
      ]
    }
  }
}
```

Then `sof run --agent team --input "Add a --verbose flag to the CLI, with tests."` What happens:

1. The lead plans tasks. With the `planApproval` sign-off, you approve the plan before any work starts (`approve <n>`).
   If you deny it, use `tell lead …` to say why; the lead plans again.
2. Up to three developers each work in a working copy of their own under `.sof/worktrees`, in the sandbox. They have no
   network except `allowedHosts`, and commands no rule allows are asked about.
3. A reviewer, never the author, reads each change and approves it or asks for changes. The reviewer can't edit files
   or run commands.
4. An approved task is squashed into one commit on your current branch, and the build and tests run on the result.
   A conflict or a failing check sends the task back to its author.

Agents can't change `sof.json`, `sof.*.json` or any file they extend, and such changes are refused at integration.
`git push` and `git remote` are denied for convenience; the real boundary is the network-less sandbox plus asking you
about unmatched commands.

## 7. Long runs: checkpoints, resume, rollback, report

| Command | What it does |
|---|---|
| `sof resume <run>` | Continue a run that stopped without ending (a crash, a killed process) from its last checkpoint. What it already spent still counts. |
| `sof rollback <run>` | List the run's checkpoints |
| `sof rollback <run> --to <n>` | Go back to checkpoint `n`: stores and working copies together. It lists what it can't undo, such as tool calls with outside effects and memory changes. |
| `sof report <run>` | Outcome, work, decisions, checks, cost by agent, task, step and model, open issues |

A resume or rollback is refused if another process holds the run, or if another run has written to the same agent's
conversation since the checkpoint. A tool call that may have run just before a crash is never repeated; it goes to you.
Until the budget restore is extended, a resumed run's *time* counts only time spent inside its work.

## 8. Manual test checklist

Do these in order; each costs more than the last. Keep `--budget` low and watch `status`.

- [ ] `dotnet test` passes on your machine, and on Linux the sandbox tests aren't all skipped.
- [ ] Section 3: `config validate`, `config show --origin`, `config dry-run` and one live `sof run`. Check the report's
      cost against your Anthropic console.
- [ ] A tool agent with `preset:tool-using-assistant` and the workspace tools, in `ask` mode: approve one write and deny
      one.
- [ ] Break the configuration on purpose (a write tool with no gate, an unknown model) and check `config validate`
      names each error.
- [ ] Coding team on a tiny repository, such as a calculator with one failing test: plan approval, two tasks, review,
      integration, then a green build on your branch.
- [ ] Kill `sof` mid-run (`kill -9` or close the terminal), then `sof resume <run>`. The run continues, and nothing that
      had outside effects runs twice.
- [ ] `sof rollback <run>` and `--to <n>`; check the working copy and the report.
- [ ] Ask an agent to edit `sof.json`, or to `git push`. Both must be refused.
- [ ] Set `--budget 0.2` and check the run hands off saying the run's cost budget is used up, or asks you to sign off past it.

Write down anything surprising, with the run id; `sof report <run>` and `.sof/sof.db` hold the details.

## 9. Known limitations

- `sof init` isn't built: write `sof.json` by hand.
- `sof.json` must sit in the git repository's top folder.
- The Claude feature switches (`providers.<name>.features`: structured output, clearing tool results, task budget,
  refusal fallback) and history compaction were tested against recordings written from the API docs. Try each on its own
  first.
- Fan-out branches of one agent share a working copy, and command checks time out after 20 minutes by default
  (`checks.<name>.timeout`).
- The items left out of v1 are listed in [`docs/plan/S21-hardening.md`](plan/S21-hardening.md).

## 10. The benchmark (later)

The benchmark scores 10 coding goals with hidden tests (TEST-31). Scoring runs code the model wrote **outside the
sandbox**, so use a throwaway VM or account.

```bash
export ANTHROPIC_API_KEY=...
python3 benchmark/bench.py --goals s1-word-count --runs 1 --i-understand-unsandboxed-scoring   # pilot, about $5
python3 benchmark/bench.py --i-understand-unsandboxed-scoring                                   # 10 goals x 3 runs
python3 benchmark/report.py benchmark/results/<linux> benchmark/results/<windows>
```

The full run on both systems is estimated at about $1,200 (it could be half or double) and takes hours on each. See
[`benchmark/README.md`](../benchmark/README.md).
