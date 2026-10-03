# sof user guide

How to set up `sof`, the Officina coding team CLI, and use it by hand. To set up a coding team step by step, see the
[team guide](team-guide.md). For the configuration, see [`CONFIGURATION.md`](../CONFIGURATION.md), the
[configuration reference](configuration-reference.md) and the [settings reference](configuration-settings.md).

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
| Python 3 | only for the benchmark and the team guide's example | same |

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

## 3. Your first agent

In an empty folder, create `sof.json`:

```json
{
  "agents": {
    "assistant": { "instructions": "Answer in three sentences or fewer." }
  },
  "run": { "budget": { "cost": 0.5 } }
}
```

Check it, then chat:

```bash
sof config validate                     # exits 1 and lists the errors, if any
sof config show --origin                # every setting, its value and where it came from
sof config dry-run --input "Hi" --reply "Hello."   # runs a scripted model: no API call
export ANTHROPIC_API_KEY=...            # Windows: $env:ANTHROPIC_API_KEY = "..."
sof                                     # type a message; /quit or Ctrl+D ends the session
```

Runs are stored in `.sof/sof.db` next to `sof.json`, and artifacts, such as the full text of a long tool result, as
files in `.sof/artifacts`. Add `.sof/` to `.gitignore`; agents cannot see it. `operations.storage` moves them
(configuration reference §12).

If `ANTHROPIC_API_KEY` isn't set, a message ends with
`HandedOff (ProviderFailure: the model call failed: KeyNotFoundException)`: set the key and send it again.

## 4. Chatting: `sof`

```
sof [--agent <name>] [--new] [--budget <usd>] [--permission-mode <mode>]     # sof chat is the same
```

Plain `sof` opens a session that stays open. With several agents and no `--agent`, it asks which one; for the coding
team, use `sof --agent team`. Each line you type is a message, and the reply streams as the model writes it. Lines that
start with `/` are commands (section 5).

**The agent remembers the conversation**, from message to message and from one session to the next. `--new`, or `/new`
in the session, starts a fresh one. The session turns on the conversation store and full history for the agent you chat
with (a team's lead), so `sof config show` doesn't show them. If `sof.json` sets that agent's `context.history.strategy`,
that is kept; with `none`, each message starts afresh. `sof` refuses to chat if `sof.json` turns the conversation store
off, or if the agent's `triggers` leave out `conversation`; `config validate` notes both.

**Each message is a run of its own**, with its own run id (printed as `run <id>`), report, checkpoints and budget.
`run.budget`, or `--budget`, applies to each message. Every reply ends with its cost and the session's cost so far, as
`assistant: Completed, cost $0.03; this session $0.05`. There is no limit for the session as a whole: queued messages
or a piped script can each spend a full budget without asking you.

**Typing during a reply.** Commands work at once. A message waits, and is sent when the reply ends; to reach an agent
now, use `/tell`. The reply never loses text: when you type, the line it is writing ends and your prompt is drawn below.

**Keys.** At a terminal, lines are edited with a line editor:

| Key | What it does |
|---|---|
| Tab | Completes a `/` command, a command's subcommands and options, agents' names (in a team also ids such as `developer[1]`), the permission modes, and the run ids of this session's messages |
| Up, Down | Go through the lines you typed in this session |
| Ctrl+C | Cancels the reply that runs (or a command such as `/resume <run>`), and drops the messages that waited for it. The session goes on. A second Ctrl+C before your next message ends the session |
| Ctrl+D | On an empty line, ends the session once the messages you sent have their replies |

With piped input (`sof < script.txt`) plain lines are read, and the end of the input ends the session.

**Working copies.** Each message's run gets its own working copies, fresh from your branch. The coding team integrates
its changes into your branch, so its next message builds on them. **A single agent that changes files does not:** its
edits are removed when the reply ends, unless `capabilities.workspace.keepWorkingCopies` is on, which keeps them in
that message's copy under `.sof/worktrees`, where the next message does not see them. When `keepWorkingCopies` is off,
the session warns about this at its start, and `config validate` notes it.

**Rollback and resume.** Each message's run is rolled back and resumed on its own. A rollback that would remove a later
message's part of the conversation is refused, so roll back the latest message first.

Exit codes: 0 when the session ends; 1 for configuration errors or an agent `sof` refuses to chat with; 2 for an
unknown `--agent`, or when no agent was picked; 3 if the session itself fails.

## 5. Commands

In a session, type each after a `/`. In `sof run` (section 8), type the first table's commands, `status`, `mode`,
`board` and `task` without the `/`. Numbers come from `/status`, and keep growing through a session.

**While a reply runs** (between replies they say no reply is running):

| Command | What it does |
|---|---|
| `/approve <n>` / `/deny <n>` | Answer an approval or a sign-off |
| `/change <n> <json>` | Approve a tool call with changed arguments |
| `/answer <n> <text>` | Answer a question an agent asked you |
| `/tell <agent> <text>` | Send an agent a message, such as `/tell lead focus on the parser` |
| `/pause [agent]` / `/resume [agent]` | Pause or resume the whole run, or one agent such as `developer[2]` |
| `/cancel [agent]` | Stop one agent, or the whole run cleanly |
| `/checkpoint` | Take a checkpoint now |
| `/memory`, `/memory approve <n> [reason]`, `/memory reject <n> <reason>` | Review project memory proposals |

**At any time:**

| Command | What it does |
|---|---|
| `/status` | Each agent's status, what waits for you (numbered), and the session's cost |
| `/mode <ask\|auto\|readOnly>` | Change the permission mode, for this reply and the rest of the session |
| `/new` | The next message starts a new conversation |
| `/report [run]` | The report of the reply that runs or the last message's run, or of any run |
| `/board` | The task board: each task's id, title, status, assignee, role, dependencies, priority and spend of its budget |
| `/task add <title> [options]`, `/task edit <id> [options]` | Add or change a task. Options: `--description`, `--criteria` (repeat it), `--depends a,b`, `--role`, `--priority <n>`, `--checks c1,c2`, `--budget <usd>`, `--review true\|false`, `--reason`; edit takes `--title` too, and its `--depends`, `--checks` and `--criteria` replace the task's list. An added task's id is `owner-1`, `owner-2`, … |
| `/task priority <id> <n>`, `/task assign <id> <agent>`, `/task cancel <id> <reason>` | Reprioritise, reassign (to an agent such as `developer[1]`) or cancel a task |
| `/task show <id>` | A task, with every change to it: who, when, what and why |
| `/config validate`, `/config show [--origin]`, `/config dry-run …` | As on the command line |
| `/help [command]` | All commands; with a `sof` command (`run`, `resume`, `rollback`, `report`, `config`, `chat`), its options |
| `/quit` | End the session |

`/board` and `/task` act on the run of the reply that runs, and the team sees a change the next time it looks at the
board, such as when an agent ends its turn. Cancelling a task an agent works on does not stop its turn; `/cancel <agent>`
does. Between replies they act on the last message's run, which has ended: a change is recorded and shows in `/report`,
but no agent works on that board again, so ask for new work in your next message. A change is refused while another
process holds that run, and after `/rollback` (resume the run, and change its board while it runs). The board refuses
what breaks its rules, such as a dependency cycle, a check that does not exist, or cancelling a task that is done.
Tasks that depend on a cancelled task stay Proposed until you change their `--depends` or cancel them too. A task
cancelled while its change is being integrated may still land on the branch; a warning says so.

**Between replies only:** `/resume <run>`, `/rollback <run> [--to <n>]`, `/run --input <text>`. While a reply runs they
are refused with an error, not queued; type them again after the reply ends.

## 6. The coding team

The [team guide](team-guide.md) sets up a lead, developers and a reviewer step by step, and runs them on a small
example. In short: with `"extends": ["preset:coding-team"]` and your build and test commands in `sof.json`, run
`sof --agent team` and type a goal. Then:

1. The lead plans tasks, and you approve the plan before any work starts (`/approve <n>`). To reject it, say why with
   `/tell lead …`, then `/deny <n>`; the lead plans again.
2. Up to three developers each work in a working copy of their own under `.sof/worktrees`, in the sandbox, with no
   network except `allowedHosts`. You're asked about any command no rule allows.
3. A reviewer, never the author, reads each change and approves it or asks for changes. It can't edit files or run
   commands.
4. An approved task's change is squashed into one commit, checked with the build and tests, then added to your branch. A
   conflict or a failing check sends the task back to its author.

Each new message is new work for the team. The lead plans it with the earlier messages in mind; the developers and the
reviewer start each task afresh.

## 7. Long runs: checkpoints, resume, rollback, report

| Command | What it does |
|---|---|
| `sof resume <run>` | Continue a run that stopped without ending, such as after a crash or a killed process, from its last checkpoint. What it already spent still counts. |
| `sof rollback <run>` | List the run's checkpoints |
| `sof rollback <run> --to <n>` | Go back to checkpoint `n`: the stores and the working copies together. Then `sof resume <run>` continues from there. |
| `sof report <run>` | Outcome, work, tasks, decisions, checks, cost by agent, task, step and model, open issues |

Each works in a session too, after a `/`. Nothing lists runs, so note the run id each message prints.

What they don't do:

- **Resume repeats work since the last checkpoint.** Only tools marked irreversible are never repeated; a call to one
  that may have run before the crash goes to you instead. The coding-team preset has no irreversible tools.
- **Rollback doesn't undo commits already integrated into your branch.** Use git for those. It lists what it can't undo,
  such as memory changes.
- A resume or rollback is refused if another process holds the run, or if another run has written to the same agent's
  conversation since the checkpoint.
- A resumed run's time budget counts only time spent inside work, not the time between the crash and the resume.

## 8. Scripts: `sof run`

```
sof run [--agent <name>] --input "<the work>"
```

One run, then the process ends. It prints the run id, shows what the agents do, and ends with the result and the run
report. Type the commands of section 5 without the `/`, such as `status` or `approve 1`.

**Ctrl+C, or `cancel`, stops the run cleanly:** it has `run.cancelWithin` (10 seconds by default) to stop, is recorded
as cancelled, and its report prints.

Exit codes: 0 done; 1 configuration errors, or a failure while starting; 2 usage error; 3 the run ended without
completing (handed off, rejected, failed or cancelled).

## 9. Configuration essentials

- **Layers, lowest to highest:** code defaults < files named in `extends` (presets first) < `sof.json` <
  `sof.<environment>.json` < `SOF__section__setting` variables < command-line options. Objects merge key by key; a list
  in a higher layer replaces the lower one's whole.
- **Presets:** `preset:coding-team`, `preset:tool-using-assistant` and `preset:single-call-extractor`. Read them in
  [`src/Sleepyshark.Officina.Core/Presets/`](../src/Sleepyshark.Officina.Core/Presets/).
- **Tools:** each tool is defined under `tools`, grouped under `toolSets`, and an agent's `tools` lists tool *set*
  names. A write tool needs `gates` or a `gateExemption` with a reason, or nothing runs.
- **Models:** the default profile is `claude-opus-5-5`. Name more in `models`, and point an agent at one with
  `agents.<name>.model`.
- **Budgets:** `run.budget` defaults to $25 and 8 hours, for each `sof run` and each chat message. Set it low while
  testing; `--budget 2` overrides it. The budget is checked between model calls, so a run can go past it by about one
  call for each agent working at once. To watch spend, each call prints
  `[agent] model call: N tokens, $x; cost so far $y`.
- **Permission mode:** `ask` (default; you approve writes no rule allows), `auto` or `readOnly`. Set it with
  `--permission-mode`, or change it with `/mode`.
- **Options every command takes:** `--dir <folder with sof.json>`, `--environment <name>`, `--budget <usd>`,
  `--permission-mode <mode>`.

## 10. Manual test checklist

Do these in order; each costs more than the last. Keep `--budget` low, watch `/status`, and use throwaway repositories.

- [ ] `dotnet test` passes on your machine, and on Linux the sandbox tests aren't all skipped.
- [ ] Section 3: `config validate`, `config show --origin`, `config dry-run`, then `sof` with one message. Check the
      reply's cost against the Claude Console.
- [ ] Chat memory: "My name is Ann." then "What is my name?". `/quit`, start `sof` again and ask once more; then
      `sof --new` and check it has forgotten.
- [ ] Typing during a reply: ask for something long, then try `/status`, type a message (it waits, and is sent when the
      reply ends), and press Ctrl+C: the reply stops, the waiting message is dropped, and the session goes on. Then try
      `/report`, `/config validate`, Tab after `/con`, Up, and end with Ctrl+C twice.
- [ ] A tool agent in a scratch git repository with one commit. `keepWorkingCopies` is on, so there's no working-copy
      warning. Approve one write and deny one; the kept working copy is under `.sof/worktrees`:

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
- [ ] The team guide's example: plan approval, two tasks, review, integration, then green tests on your branch. Then a
      second goal in the same session; the lead's plan should know the first.
- [ ] `sof run --input "…"`, and press Ctrl+C during it. The run stops within `run.cancelWithin`, the report prints, and
      the exit code is 3.
- [ ] Kill `sof` mid-reply (`kill -9`, or close the terminal), then `sof resume <run>`. The run continues from its last
      checkpoint.
- [ ] `sof rollback <run>`, then `--to <n>`, then `sof resume <run>`; check the working copies and the report.
- [ ] Ask an agent to edit `sof.json`, or to `git push`. Both must be refused.
- [ ] Run with `--budget 0.2`. The run should either hand off, saying the run's cost budget is used up, or ask you to
      sign off past it.

Write down anything surprising, with the run id; `sof report <run>` and `.sof/sof.db` hold the details.

## 11. Known limitations

- `sof init` isn't built: write `sof.json` by hand.
- `sof.json` must sit in the git repository's top folder.
- Nothing lists runs: note the run id each message or `sof run` prints.
- Between replies, `/task` changes the last message's run, which has ended, so no agent works on what it adds (section 5).
- A single agent with the workspace loses its edits between chat messages (section 4).
- The Claude feature switches (`providers.<name>.features`: structured output, clearing tool results, task budget,
  refusal fallback) and history compaction were tested against recordings written from the API docs. Try each on its own
  first.
- Fan-out branches of one agent share a working copy. Command checks time out after 20 minutes by default
  (`checks.<name>.timeout`).
- The items left out of v1 are listed in [`docs/plan/S21-hardening.md`](plan/S21-hardening.md).

## 12. The benchmark (later)

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
