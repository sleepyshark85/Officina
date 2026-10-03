# Set up a small coding team

This guide builds a coding team by hand: a lead, two developers and a reviewer. It explains what each piece does, then
shows the same team built on `preset:coding-team`, and runs it on a tiny .NET project. Read the
[user guide](user-guide.md) first for installing `sof` and using a chat session.

> **This costs real money.** Each message to the team is a run that calls Claude many times. The example limits each
> message to $3 (`run.budget`). Set a monthly spend limit in the [Claude Console](https://console.anthropic.com),
> and work in a throwaway repository.

## 1. The example project

A calculator with a bug, and a feature to add. On Linux or Windows (macOS has no sandbox, so the team can't run there),
with the .NET 10 SDK:

```bash
mkdir calc && cd calc
git init -b main
dotnet new sln -n Calc
dotnet new classlib -n Calc -o Calc
dotnet new xunit -n Calc.Tests -o Calc.Tests
dotnet add Calc.Tests reference Calc
dotnet sln add Calc Calc.Tests
rm Calc/Class1.cs
rm Calc.Tests/UnitTest1.cs
```

That makes `Calc.slnx` with a class library and an xUnit test project. Then add three files.

`Calc/Calculator.cs`:

```csharp
namespace Calc;

public static class Calculator
{
    public static int Add(int a, int b) => a + b;

    public static int Divide(int a, int b) => a * b;
}
```

`Calc.Tests/CalculatorTests.cs`:

```csharp
namespace Calc.Tests;

public class CalculatorTests
{
    [Fact]
    public void Add() => Assert.Equal(5, Calculator.Add(2, 3));

    [Fact]
    public void Divide() => Assert.Equal(2, Calculator.Divide(6, 3));
}
```

`.gitignore`, so build output doesn't end up in a commit:

```
.sof/
bin/
obj/
```

Check that the build passes and one test fails:

```bash
dotnet build   # Build succeeded.
dotnet test    # Divide fails (Expected: 2, Actual: 18); Add passes
```

### Where is your SDK?

On Linux, the sandbox shows only the system folders (`/usr`, `/bin`, `/lib` and a few more), and finds programs only in
`/usr/local/bin`, `/usr/bin` and `/bin`. `sof init` (section 3) notes an SDK the sandbox can't run, such as

```
note: your dotnet is at /usr/lib/dotnet, where the sandbox doesn't look. Add /usr/lib/dotnet to capabilities.sandbox.toolchains, so that commands in the sandbox run it.
```

To check by hand, run `which dotnet` and `dotnet --list-sdks`:

- `/usr/bin/dotnet`, as a distribution's package installs it: nothing to do.
- Anywhere else, such as `~/.dotnet/dotnet` from Microsoft's install script, or `/usr/lib/dotnet/dotnet` with no link in
  `/usr/bin`: add the folder that holds `dotnet` (the one before `/sdk` in `dotnet --list-sdks`) to
  `capabilities.sandbox.toolchains`, as a full path, such as `"toolchains": ["/home/me/.dotnet"]`. Without it, every
  command fails with `dotnet: not found`. A snap's `dotnet` was not checked.

## 2. The team, by hand

Save this as `sof.json` in the `calc` folder. Each part is explained below it.

```json
{
  "project": {
    "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" }
  },
  "models": {
    "default": { "effort": "high" },
    "review": { "effort": "medium" }
  },
  "agents": {
    "team": {
      "description": "A lead, two developers and a reviewer.",
      "instructions": "A small coding team works on the goal.",
      "pattern": { "type": "team", "lead": "lead", "roles": { "developer": { "max": 2 }, "reviewer": { "max": 1 } } }
    },
    "lead": {
      "description": "Plans the goal as tasks and decides on tasks that fail. Changes no files.",
      "instructions": "You lead a small software team. Read the code, then plan the goal as small tasks, each with acceptance criteria, the checks named exactly build and tests, and requiresReview set to true. Order the tasks with dependencies when one needs another. When a task fails, retry it, split it or cancel it. You change no files yourself.",
      "tools": ["files-read", "leading"]
    },
    "developer": {
      "description": "Does one task at a time in its own working copy.",
      "instructions": "You are a developer. Do the task you are given in its working copy: read the code, change it, build with {{project.values.buildCommand}}, test with {{project.values.testCommand}}, and submit the task when its acceptance criteria are met. Keep the change small.",
      "tools": ["files-read", "files-write", "shell", "developing"]
    },
    "reviewer": {
      "model": "review",
      "description": "Reviews another developer's task. Never changes code.",
      "instructions": "You review the team's tasks. Read the task's change against its acceptance criteria, then approve it or ask for changes, with your reasons. You never change the code.",
      "tools": ["files-read", "reviewing"]
    }
  },
  "tools": {
    "read_file": { "source": "extension:workspace.read_file" },
    "search": { "source": "extension:workspace.search" },
    "write_file": { "source": "extension:workspace.write_file", "gateExemption": "It changes only the task's working copy, which is checked and reviewed before integration." },
    "edit_file": { "source": "extension:workspace.edit_file", "gateExemption": "It changes only the task's working copy, which is checked and reviewed before integration." },
    "run_command": { "source": "extension:sandbox.run", "gates": ["commands"] },
    "create_task": { "source": "builtin:tasks.create" },
    "update_task": { "source": "builtin:tasks.update" },
    "submit_task": { "source": "builtin:tasks.submit_for_review" },
    "review_task": { "source": "builtin:tasks.review" },
    "message": { "source": "builtin:team.message" },
    "ask_owner": { "source": "builtin:human.ask_owner" }
  },
  "toolSets": {
    "files-read": ["read_file", "search"],
    "files-write": ["write_file", "edit_file"],
    "shell": ["run_command"],
    "leading": ["create_task", "update_task", "message", "ask_owner"],
    "developing": ["submit_task", "message", "ask_owner"],
    "reviewing": ["review_task", "message"]
  },
  "gates": { "commands": { "use": "extension:sandbox.commandRules" } },
  "checks": {
    "build": { "command": "{{project.values.buildCommand}}" },
    "tests": { "command": "{{project.values.testCommand}}" }
  },
  "policies": {
    "masking": { "enabled": false },
    "permissionRules": [
      { "tool": "write_file", "action": "allow" },
      { "tool": "edit_file", "action": "allow" },
      { "tool": "run_command", "action": "allow" }
    ]
  },
  "run": { "budget": { "cost": 3, "time": "01:00:00" } },
  "capabilities": {
    "team": { "enabled": true },
    "taskBoard": { "enabled": true, "maxAttempts": 2, "budget": { "cost": 2 } },
    "conversationStore": { "enabled": true },
    "checkpoints": { "enabled": true, "at": ["turn", "integration"] },
    "humanInteraction": { "enabled": true, "signOffs": ["planApproval", "runBudgetExceeded", "irreversibleAction"] },
    "workspace": { "enabled": true, "baselineChecks": ["build", "tests"] },
    "sandbox": {
      "enabled": true,
      "allowedHosts": ["api.nuget.org", "*.nuget.org"],
      "commandRules": [
        { "match": "dotnet build", "action": "allow" },
        { "match": "dotnet build *", "action": "allow" },
        { "match": "dotnet test", "action": "allow" },
        { "match": "dotnet test *", "action": "allow" },
        { "match": "git push*", "action": "deny" },
        { "match": "git remote*", "action": "deny" }
      ]
    }
  }
}
```

If your SDK needs it (section 1), add `"toolchains": [...]` to `sandbox`. Check the file before you spend anything:

```bash
sof config validate            # "The configuration is valid."
sof config show --origin       # every setting and where it came from
```

### The roles

`team` is the agent you talk to. Its `team` pattern names the lead and the roles; `max` is how many agents of a role
can exist (`developer[1]`, `developer[2]`, `reviewer[1]`). The other three are definitions the team runs.

| Role | Model profile | Tool sets | Why |
|---|---|---|---|
| `lead` | `default` (high effort) | `files-read`, `leading` | Planning sets up everything after it, so it gets the most effort. It reads code but has no write tools, so it can't change files. It creates and updates tasks, and messages the team or asks you. |
| `developer` | `default` (high effort) | `files-read`, `files-write`, `shell`, `developing` | The only role that changes code and runs commands, each in its task's own working copy. It submits its task when done, which runs the task's checks. |
| `reviewer` | `review` (medium effort) | `files-read`, `reviewing` | Reading a small change and judging it against criteria needs less effort. No write tools and no commands, so it can't change the author's work (it is never the author). For a cheaper reviewer, set `"review": { "model": "claude-sonnet-5-5" }`. |

**Instructions** say what the role is for. The engine adds the rest: the goal, the task, the board and who the team is.
The developer's instructions use `{{project.values.…}}` placeholders, so the commands are written once, in `project`.

**Tools** are defined once under `tools`, grouped into `toolSets`, and each agent lists sets. A write tool must have a
gate or a `gateExemption` with a reason: the file tools are exempt because they only touch a task's working copy, which
is checked and reviewed before it reaches your branch. `run_command` goes through the `commands` gate, which is the
sandbox's command rules.

**Permission rules** decide each tool call first. In the default `ask` mode, every write no rule allows is put to you.
These rules allow the file writes and `run_command`, so you aren't asked about every edit; commands still pass the
command rules next.

**Command rules** decide each command a developer runs, first match wins. Here: `dotnet build` and `dotnet test` are
allowed, each as itself and followed by arguments (`dotnet test *` allows `dotnet test --filter Divide`, but
`dotnet build*` would also allow `dotnet build-server`); `git push` and `git remote` are denied; and **anything else is
asked about** (`/approve` or `/deny`). Git itself can't run in the sandbox, because `.git` is hidden, so there's no
point allowing `git status` or `git diff`. Don't allow `dotnet *`: that allows `dotnet run` and every tool.

**Allowed hosts** are the only network the sandbox has: NuGet, so the build can restore packages. Each working copy keeps its packages between commands, so only its first build downloads them.

**Masking** is off because it would replace things that look like emails or phone numbers in source code.

### The capabilities

All are off by default. `config validate` names any that a tool, check or pattern above needs and is off.

| Capability | Why the team needs it |
|---|---|
| `team` | Runs the lead and the roles together, and gives them `team.message` |
| `taskBoard` | The tasks: the lead's plan, their dependencies, checks, reviews and state. `/board` shows it |
| `workspace` | A git working copy for each task under `.sof/worktrees`, and integration into your branch |
| `sandbox` | Commands and checks run isolated, with no network unless `allowedHosts` lists hosts |
| `humanInteraction` | Lets agents ask you (`ask_owner`) and turns on the sign-offs: `planApproval` (you approve the plan before work starts), `runBudgetExceeded` (you decide whether to go past the budget) and `irreversibleAction` |
| `conversationStore` | Keeps the lead's conversation, so the next goal builds on the last. `sof` turns it on for a chat anyway |
| `checkpoints` | Saved states after each turn and each integration, for `sof resume` after a crash and `sof rollback` |
| `projectMemory` (optional) | Conventions and decisions every agent sees, proposed by developers and approved by the lead; see below |

To add project memory, add the tools to the sets that use them, and allow them:

```jsonc
"tools": {
  // ...the tools above, and:
  "propose_memory": { "source": "builtin:memory.propose_change", "gateExemption": "A proposal is memory only once the lead approves it." },
  "review_memory": { "source": "builtin:memory.review", "gateExemption": "Only the lead decides, never on its own proposal." }
},
"toolSets": {
  // replaces the two sets above:
  "leading": ["create_task", "update_task", "message", "ask_owner", "review_memory"],
  "developing": ["submit_task", "message", "ask_owner", "propose_memory"]
},
// add { "tool": "propose_memory", "action": "allow" } and the same for review_memory to policies.permissionRules
"capabilities": { "projectMemory": { "enabled": true, "approveBy": "lead" } }
```

`/memory` lists what waits, while a reply runs.

### Budgets

Every level has a limit. The run's is the one you set most often.

| Setting | Here | What it limits |
|---|---|---|
| `run.budget` | $3, 1 hour | Each message to the team (each `sof run`). `--budget <usd>` overrides the cost for one session or run. When it runs out, the `runBudgetExceeded` sign-off asks you: approve, and the run gets another budget of the same size |
| `capabilities.taskBoard.budget` | $2 | Everything spent on one task. Used up, the task goes back to the lead |
| `capabilities.taskBoard.maxAttempts` | 2 | Failed checks, reviews or integrations before a task goes back to the lead |
| `agents.<name>.budget.turn` | default: $5, 50 iterations, 45 minutes | One turn of an agent |
| `agents.<name>.budget.total` | unset | All of one agent's turns in a run, such as `"lead": { "budget": { "total": { "cost": 1 } } }` |

A budget is checked between model calls, so a team can go over by about one call for each agent working at once, and
several agents may ask for the `runBudgetExceeded` sign-off at the same time.

### Checks

`checks.build` and `checks.tests` run the project's commands in the sandbox. A check passes when its command exits with
0, and fails after 20 minutes (`timeout`). They are used twice:

1. **At submit:** the lead names `build` and `tests` on each task (its instructions say so). When a developer submits,
   they run in the task's working copy, and only if they pass does the task go to review.
2. **At integration:** `capabilities.workspace.baselineChecks` runs them on the change applied to your branch, just
   before it is committed there. A failure sends the task back to its author.

Your branch must pass both checks after each task, so a task that fails on its own, such as the feature landing before
the fix, goes back. That's why the lead's instructions ask it to order tasks with dependencies.

## 3. The same team from the preset

`preset:coding-team` is this team with more tools (delete and move files, background processes, `hand_off`), a shared
`coder` base for the roles, project memory on, and up to three developers. With the same project and limits:

```jsonc
{
  "extends": ["preset:coding-team"],
  "project": {
    "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" }
  },
  "agents": {
    "team": { "pattern": { "roles": { "developer": { "max": 2 } } } }
  },
  "run": { "budget": { "cost": 3, "time": "01:00:00" } },
  "capabilities": {
    "taskBoard": { "maxAttempts": 2, "budget": { "cost": 2 } },
    "sandbox": {
      "allowedHosts": ["api.nuget.org", "*.nuget.org"],
      // This list replaces the preset's whole, so it repeats the preset's git deny rules.
      "commandRules": [
        { "match": "dotnet build", "action": "allow" },
        { "match": "dotnet build *", "action": "allow" },
        { "match": "dotnet test", "action": "allow" },
        { "match": "dotnet test *", "action": "allow" },
        { "match": "git push*", "action": "deny" },
        { "match": "git remote*", "action": "deny" }
      ]
    }
  }
}
```

**The fast path:** `sof init` in the `calc` folder writes most of this for you. It says
`Found .NET (Calc.slnx): dotnet build and dotnet test.`, and asks for each command; press Enter twice to take them. It
writes the two commands, `run.budget` at $3, the NuGet hosts and the same six command rules, then validates the file.
It leaves out the time limit, the team size and the task board's limits, which you add by hand. If the sandbox can't
run the `dotnet` on your `PATH`, such as `~/.dotnet/dotnet` or `/usr/lib/dotnet/dotnet`, it says to add that folder to
`toolchains` (section 1); it doesn't add it itself. See the [user guide](user-guide.md), section 6.

Objects merge key by key, so `"roles": { "developer": { "max": 2 } }` keeps the preset's lead and reviewer. A list, such
as `commandRules`, a sign-off list or an agent's `tools`, replaces the preset's whole. Run `sof config show --origin` to
see which value came from the preset and which from your file. To change one role, override only it, such as
`"agents": { "reviewer": { "model": "cheap" } }` with `"models": { "cheap": { "model": "claude-sonnet-5-5" } }`.

| Use | When |
|---|---|
| The preset | Most of the time. You get the full tool set and later fixes to it, and write only your commands, limits and rules |
| By hand | You want another team shape: other roles, fewer tools, different instructions, or a role that isn't a coder |

## 4. Run it

Commit everything, and work on a branch of its own: the team commits its work to the branch that is checked out, and
needs at least one commit to make working copies from.

```bash
git add . && git commit -m "A tiny calculator with a failing test"
git switch -c sof-try
sof --agent team               # with ANTHROPIC_API_KEY set
```

Type the goal as one line:

```
Fix the failing Divide test; the fix must come first. Then add Calculator.Average(numbers), which throws ArgumentException for an empty list, with tests for both cases.
```

What to expect:

1. **Plan.** `run <id>` is printed; note it. The lead reads the code and creates tasks. Then it waits for your sign-off
   (abridged; a live plan also lists each task's description and acceptance criteria, indented under it):

   ```
   #1 lead needs your sign-off: Approve the lead's plan before work starts?
     average Add Average: Proposed, role developer, depends on fix-divide, needs a review
     fix-divide Fix Divide: Ready, role developer, needs a review
   Answer with /approve 1 or /deny 1.
   ```

   The sign-off is never folded, however long the plan: you see every task before you approve it. `/status` shows the
   waiting request and its number again. Check that `average` depends on `fix-divide`; if it
   doesn't, fix it yourself with `/task edit average --depends fix-divide`. Type `/approve 1` (or just `/approve`, as no
   other request was shown since). To change the plan, `/tell lead <what to change>`, then `/deny 1`; the lead plans
   again. With no answer within `run.approvalTimeout` (30 minutes), the team stops.

   After `/approve`, the session offers to save the plan, with each task's title, description, acceptance criteria and
   dependencies (cancelled tasks are left out):

   ```
   Save this as docs/plans/2026-10-03-fix-the-failing-divide-test-the-fix-must-come-first-then.md? [Enter = yes, n = no, or type another path]
   ```

   Press Enter to save it there, type another path or folder, or `n`. The file isn't committed; commit it yourself if
   you want to keep it. `/save` saves it later too.
2. **Tasks.** A developer takes each ready task, in `.sof/worktrees/<run>-task.<task id>`. Lines such as
   `[developer[1]] running run_command` show what it does. A command no rule allows waits for `/approve` or `/deny`. The
   task with a dependency waits until the one it needs is done.
3. **Submit.** The developer submits; `build` and `tests` run in its working copy.
4. **Review.** `reviewer[1]` approves, or asks for changes, which sends the task back to the developer.
5. **Integration.** The change is squashed into one commit, `build` and `tests` run on it applied to your branch, and
   the commit is added to `sof-try`. The working copy is removed.
6. **Report.** When every task is done, the lead reports, and the reply ends with
   `team: Completed, cost $…; this session $…`. A report of more than 20 lines shows its first 12, then
   `… N more lines (… KB). /show to read all · /save to keep it`: `/show` opens it in a pager (`q` closes it), and
   `/history` lists the session's replies. A developer's long text folds the same way while the team works (user
   guide, section 4, Long replies).

To watch spend, each model call prints `[agent] model call: N tokens, $x; cost so far $y`. While it works: `/status`
shows each agent and what waits for you, `/board` the tasks, and `/tell lead …` reaches the lead. `/task` changes the
board as the team works, such as `/task add Add a Median --role developer --depends fix-divide` or
`/task cancel average Not needed`; the team sees the change the next time it looks at the board. Ctrl+C cancels the
reply; changes already integrated stay on the branch.

## 5. Check the result

In the session, after the reply:

- `/report`: the outcome, each task's state and spend, which checks passed and failed, and cost by agent, task and
  model. `/board` shows the tasks, and `/task show <id>` every change to one.
- `/quit`, then in the shell:

```bash
git log --stat    # one commit per task, such as "Task fix-divide", by developer[1] or [2]
dotnet test       # all tests pass
sof report <run>  # the same report as /report
```

Each task's commit message ends with `Officina-Run`, `Officina-Agent` and `Officina-Task` lines. If you don't like the
result, throw the branch away: `git switch main && git branch -D sof-try`.

A second goal in the same session (or a later `sof --agent team`) builds on the first: the lead remembers it, and the
branch has its commits. `sof --agent team --new` starts the lead afresh.

## 6. Safety

- **Sandbox:** commands and checks run without network (unless `allowedHosts`), with limits on CPU, memory and
  processes. On Linux, only system folders, the toolchains, the working copy and its own home folder are visible.
- **Command rules:** anything not allowed is asked about. The git deny rules are a convenience; the sandbox is the
  boundary.
- **Protected files:** `.git`, `.env*` and `.sof/` are hidden from agents, and `sof.json`, `sof.*.json` and the files
  they extend are read-only. Integration refuses a change that touches them. Add your own with
  `capabilities.workspace.protectedPaths`.
- **Review:** the reviewer can't change the code it reviews, and never reviews its own task.
- **Budgets:** per message, per task and per turn, with a sign-off before going past the run's budget. Keep the Console
  spend limit as a backstop.
- **Your branch:** work on a throwaway branch with a clean working tree. Rollback doesn't undo commits already
  integrated; use git for those.
