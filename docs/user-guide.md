# sof user guide

How to set up `sof`, the Officina coding team CLI, and use it by hand. For the configuration, see
[`CONFIGURATION.md`](../CONFIGURATION.md), the [configuration reference](configuration-reference.md) and the
[settings reference](configuration-settings.md).

> **Status.** Every slice passes its tests on Linux and Windows, but the tests use a scripted model. No full live run
> against Claude has been done yet. Treat your first sessions as a field test: work in throwaway repositories, start
> small and keep the budget low.

## 1. Prerequisites

| Need | Linux | Windows |
|---|---|---|
| .NET SDK | 10.0.100 or later | same |
| git | on PATH, with `user.name` and `user.email` set | same |
| Sandbox (the coding team turns it on) | `bubblewrap`, `socat`, cgroups v2, a systemd user manager | Windows 10 or 11; no admin rights needed |
| Claude API key | `ANTHROPIC_API_KEY` in the environment | same |
| Python 3 | only for the benchmark | same |

Get an API key from the [Claude Console](https://console.anthropic.com), and set a monthly spend limit there as a
backstop to `sof`'s own budgets.

On Ubuntu 23.10 and later, run this once as root. It is the same setup CI uses:

```bash
sudo apt-get update
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

The package version is always 0.1.0, so `dotnet tool update` keeps the old build. To pick up changes, pack again, then
`dotnet tool uninstall --global Sleepyshark.Officina.Cli` and install again. Without installing,
`dotnet run --project src/Sleepyshark.Officina.Cli -- <args>` works the same way.

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
sof                                     # a chat session: type messages, /quit to end (section 6)
```

`sof run` prints the run id first, streams what the agent does, and ends with the run report. The run is stored in
`.sof/sof.db` next to `sof.json`; add `.sof/` to `.gitignore`.

## 4. Configuration essentials

- **Layers, lowest to highest:** code defaults < files named in `extends` (presets first) < `sof.json` <
  `sof.<environment>.json` < `SOF__section__setting` variables < command-line options. A list in a higher layer replaces
  the lower one's whole.
- **Presets:** `preset:coding-team`, `preset:tool-using-assistant` and `preset:single-call-extractor`. Read them in
  [`src/Sleepyshark.Officina.Core/Presets/`](../src/Sleepyshark.Officina.Core/Presets/).
- **Tools:** each tool is defined under `tools`, grouped under `toolSets`, and an agent's `tools` lists tool *set*
  names. A write tool needs `gates` or a `gateExemption` with a reason; `config validate`, `config dry-run` and
  `sof run` all refuse it.
- **Models:** the default profile is `claude-opus-5-5`. Name more in `models`, and point an agent at one with
  `agents.<name>.model`.
- **Budgets:** `run.budget` defaults to $25 and 8 hours. Set it low while testing; `--budget 2` overrides it for one
  run. A run can go past its budget by about one model call, because the budget is checked between calls.
- **Permission mode:** `ask` (default; you approve writes no rule allows), `auto` or `readOnly`. Set it with
  `--permission-mode`, or switch it during a run with `mode <name>`.
- **Options every command takes:** `--dir <folder with sof.json>`, `--environment <name>`, `--budget <usd>`,
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
| `cancel [agent]` | Stop one agent, or the whole run cleanly |
| `checkpoint` | Take a checkpoint now |
| `board` | Show the task board |
| `memory`, `memory approve <n> [reason]`, `memory reject <n> <reason>` | Review project memory proposals |

**To stop a run, press Ctrl+C or type `cancel`.** Both stop it cleanly: the run has `run.cancelWithin` (10 seconds by
default) to stop, is recorded as cancelled, and its report prints. A second Ctrl+C does not end the process sooner.

Exit codes: 0 done; 1 configuration errors, or a failure while starting; 2 usage error; 3 the run ended without
completing (handed off, rejected or failed).

## 6. Chatting with an agent

```
sof [--agent <name>] [--new]            # sof chat is the same
```

Plain `sof` opens a session that stays open. With several agents and no `--agent`, it asks which one. Each line you type
is a message, and the reply streams as the model writes it. The agent remembers the conversation: from message to
message, and from one session to the next. `--new` (or `/new` in the session) starts a fresh conversation.

Lines that start with `/` are commands, so they're never taken for a message:

| Command | What it does |
|---|---|
| `/status` … `/memory` | The commands of section 5, with a `/`: `/approve 1`, `/tell lead focus on the parser`, `/mode auto`… |
| `/report [run]` | The report of the last message's run, or of any run |
| `/resume <run>`, `/rollback <run> [--to <n>]`, `/run --input <text>` | As on the command line; they wait until no reply runs |
| `/config validate`, `/config show [--origin]`, `/config dry-run …` | As on the command line |
| `/new` | The next message starts a new conversation |
| `/help [command]` | Everything, or one command's options |
| `/quit` | End the session. So does Ctrl+D, once the messages you sent have their replies |

At a terminal, lines are edited with a line editor: **Tab** completes a `/` command, a command's subcommands and
options, agents' names and the run ids of this session's messages, **Up** and **Down** go through
the lines you typed, and **Ctrl+D** on an empty line ends the session. A reply's output clears the prompt line, which
comes back at your next key; what you had typed is kept. With piped input (`sof < script.txt`), plain lines are read.

How it behaves:

- **Each message is a run of its own**, with its own run id, report, checkpoints and budget: `run.budget` applies to each
  message, as it does to each `sof run`. Every reply ends with its cost and the session's cost so far, and `/status` shows
  the session's cost too.
- **The conversation is kept in the conversation store**, which the session turns on, with full history for the agent
  you chat with, unless `sof.json` sets its `context.history.strategy`: then that strategy is kept, and with `none` each
  message starts afresh. If `sof.json` turns the conversation store off, `sof` refuses to chat; so does an agent whose
  `triggers` leave out `conversation`. `config validate` notes both.
- **The coding team** (`--agent team`): each message is new work for the team. The lead plans it, with the earlier
  messages in mind, as the lead keeps the conversation; the developers and the reviewer start each task afresh. To speak
  to the lead or a developer while the team works, use `/tell`.
- **While a reply runs**, commands work at once. A message you type waits, and is sent when the reply ends. `/resume` then
  resumes what `/pause` paused; `/resume <run>` and `/rollback` wait until no reply runs.
- **Ctrl+C** cancels the reply (or a command such as `/resume <run>`) and drops the messages that waited for it; the
  session goes on. A second Ctrl+C before your next message ends the session.
- **Rollback and resume:** every message's run is rolled back and resumed on its own. A rollback that would remove a later
  message's part of the conversation is refused, so roll back the latest message first. A resumed message runs with the
  conversation, as the session ran it.
- **Working copies:** each message's run gets its own working copies, as each `sof run` does. A single agent's edits from
  one message are in the kept working copy (`keepWorkingCopies`), not in the next message's; the coding team integrates
  its changes into your branch, so its next message builds on them.

## 7. The coding team

Set up a throwaway repository first:

- It needs at least one commit; on an empty repository the working copies can't be created.
- Work on a new branch with a clean working tree. Approved changes are committed to the checked-out branch.
- Add `.sof/` to `.gitignore`.

A configuration for a .NET project, with `sof.json` in the repository's top folder:

```jsonc
{
  "extends": ["preset:coding-team"],
  "project": { "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" } },
  "run": { "budget": { "cost": 5 } },
  "capabilities": {
    "sandbox": {
      "allowedHosts": ["api.nuget.org", "*.nuget.org"],
      // Only system folders are visible in the Linux sandbox. If the SDK lives elsewhere (~/.dotnet, a snap), list it:
      // "toolchains": ["/home/me/.dotnet"],
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

1. The lead plans tasks, and you approve the plan before any work starts (`approve <n>`). To reject it, first say why
   with `tell lead …`, then `deny <n>`; the lead plans again at once.
2. Up to three developers each work in a working copy of their own under `.sof/worktrees`, in the sandbox. They have no
   network except `allowedHosts`, and you're asked about any command no rule allows.
3. A reviewer, never the author, reads each change and approves it or asks for changes. The reviewer can't edit files
   or run commands.
4. An approved task's change is squashed into one commit, checked with the build and tests, then added to your branch. A
   conflict or a failing check sends the task back to its author.

Agents can't change `sof.json`, `sof.*.json` or any file they extend, and such changes are refused at integration.
`git push` and `git remote` are denied for convenience; the real boundary is the network-less sandbox plus asking you
about unmatched commands.

## 8. Long runs: checkpoints, resume, rollback, report

| Command | What it does |
|---|---|
| `sof resume <run>` | Continue a run that stopped without ending, such as after a crash or a killed process, from its last checkpoint. What it already spent still counts. |
| `sof rollback <run>` | List the run's checkpoints |
| `sof rollback <run> --to <n>` | Go back to checkpoint `n`: the stores and the working copies together. Then `sof resume <run>` continues from there. |
| `sof report <run>` | Outcome, work, decisions, checks, cost by agent, task, step and model, open issues |

What they don't do:

- **Resume repeats work since the last checkpoint.** Only tools marked irreversible are never repeated; a call to one
  that may have run before the crash goes to you instead. The coding-team preset has no irreversible tools.
- **Rollback doesn't undo commits already integrated into your branch.** Use git for those. It lists what it can't undo,
  such as memory changes.
- A resume or rollback is refused if another process holds the run, or if another run has written to the same agent's
  conversation since the checkpoint.
- A resumed run's time budget counts only time spent inside work, not the time between the crash and the resume.

## 9. Manual test checklist

Do these in order; each costs more than the last. Keep `--budget` low, watch `status`, and use throwaway repositories.

- [ ] `dotnet test` passes on your machine, and on Linux the sandbox tests aren't all skipped.
- [ ] Section 3: `config validate`, `config show --origin`, `config dry-run` and one live `sof run`. Check the report's
      cost against the Claude Console.
- [ ] A tool agent in a scratch git repository with one commit. Approve one write and deny one; the kept working copy is
      under `.sof/worktrees`:

  ```jsonc
  {
    "extends": ["preset:tool-using-assistant"],
    "agents": { "assistant": { "instructions": "Help with the files in this folder.", "tools": ["files"] } },
    "tools": {
      "read_file": { "source": "extension:workspace.read_file" },
      "write_file": { "source": "extension:workspace.write_file", "gateExemption": "Manual test in a scratch repository." }
    },
    "toolSets": { "files": ["read_file", "write_file"] },
    "capabilities": { "workspace": { "enabled": true, "keepWorkingCopies": true } },
    "run": { "budget": { "cost": 1 } }
  }
  ```

- [ ] Break the configuration on purpose, such as an unknown model or a tool set that doesn't exist, and check that
      `config validate` names each error.
- [ ] Coding team on a tiny repository, such as a calculator with one failing test: plan approval, two tasks, review,
      integration, then a green build on your branch.
- [ ] Press Ctrl+C during a run. The run stops within `run.cancelWithin`, the report prints, and the exit code is 3.
- [ ] Chat (section 6): plain `sof`, two messages where the second needs the first ("My name is Ann." then "What is my
      name?"). Quit, start `sof` again and ask once more; then `sof --new` and check it has forgotten. During a reply, try
      `/status`, type a message (it waits), and press Ctrl+C: the reply stops and the session goes on. Try `/report`,
      `/config validate`, then Ctrl+C twice.
- [ ] Chat with the coding team (`sof --agent team`): two small goals in a row; the lead's second plan should know the
      first.
- [ ] Kill `sof` mid-run (`kill -9`, or close the terminal), then `sof resume <run>`. The run continues from its last
      checkpoint.
- [ ] `sof rollback <run>`, then `--to <n>`, then `sof resume <run>`; check the working copies and the report.
- [ ] Ask an agent to edit `sof.json`, or to `git push`. Both must be refused.
- [ ] Run with `--budget 0.2`. The run should either hand off, saying the run's cost budget is used up, or ask you to
      sign off past it.

Write down anything surprising, with the run id; `sof report <run>` and `.sof/sof.db` hold the details.

## 10. Known limitations

- `sof init` isn't built: write `sof.json` by hand.
- `sof.json` must sit in the git repository's top folder.
- The Claude feature switches (`providers.<name>.features`: structured output, clearing tool results, task budget,
  refusal fallback) and history compaction were tested against recordings written from the API docs. Try each on its own
  first.
- Fan-out branches of one agent share a working copy. Command checks time out after 20 minutes by default
  (`checks.<name>.timeout`).
- `board` only shows the task board; the console can't add, edit, reprioritise, reassign or cancel tasks.
- The items left out of v1 are listed in [`docs/plan/S21-hardening.md`](plan/S21-hardening.md).

## 11. The benchmark (later)

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
