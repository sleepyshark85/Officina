using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// TASK-08: the owner views the board and adds, edits, reprioritises, reassigns and cancels tasks at the console, during a run, where
/// the team works from the board as the owner leaves it, and between a chat's replies, on the last message's run straight from
/// storage. Each change is the owner's, recorded with who, when, what and why (TASK-07), and the board's rules refuse what breaks them.
/// The model and the console are the stand-ins; the storage is real SQLite.
/// </summary>
public sealed class TaskCommandTests : IDisposable
{
    private readonly Sof sof = new();
    private readonly ScriptedModelProvider model = new();

    public TaskCommandTests() => sof.Providers["claude"] = model;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => sof.Dispose();

    // TASK-08, TASK-07, TEAM-10: while the plan waits for sign-off, the owner reprioritises, cancels and adds tasks at sof run's console,
    // and the team works from the board as the owner left it at its next look: the reprioritised task first, the added one in its
    // turn, the cancelled one never. The report lists the owner's changes.
    [Fact]
    public async Task During_a_team_run_the_team_picks_up_the_owners_changes_at_its_next_look()
    {
        sof.Write("sof.json", Team(signOff: true));
        model.When(request => Work(request).StartsWith("You lead a team", StringComparison.Ordinal))
            .CallTools(
                ("create", """{ "id": "a", "title": "Parse", "reason": "plan" }"""),
                ("create", """{ "id": "b", "title": "Print", "reason": "plan" }"""),
                ("create", """{ "id": "c", "title": "Add", "reason": "plan" }"""))
            .Reply("Planned.");
        foreach (var task in new[] { "a", "c", "owner-1" })
        {
            model.When(request => Work(request).Contains($"Do task {task},", StringComparison.Ordinal))
                .CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Submitted.");
        }

        model.When(request => Work(request).StartsWith("Every task is done", StringComparison.Ordinal)).Reply("All done.");

        var run = sof.RunAsync("run", "--agent", "team", "--input", "Write a calculator.");
        await sof.Out.WaitForAsync("Answer with approve or deny.", Ct);
        sof.In.Type("task priority c 5");
        await sof.Out.WaitForAsync("Changed: c priority 0 → 5.", Ct);
        sof.In.Type("task cancel b Not needed.");
        await sof.Out.WaitForAsync("Changed: b state Ready → Cancelled.", Ct);
        sof.In.Type("""task add Write the docs --role developer --priority 1 --criteria "It explains the parser." """);
        await sof.Out.WaitForAsync("Changed: owner-1 added as Ready.", Ct);
        sof.In.Type("board");
        await sof.Out.WaitForAsync("owner-1 Write the docs: Ready, role developer, priority 1, $0.00 of $8.00", Ct);
        sof.In.Type("approve 1");
        var (exitCode, output, _) = await sof.EndedAsync(run);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(
            ["c", "owner-1", "a"],
            model.Requests.Select(Work).Where(work => work.Contains("Do task ", StringComparison.Ordinal))
                .Select(work => work.Split("Do task ")[1].Split(',')[0]).Distinct());
        Assert.Contains("b Print: Cancelled, $0.00 of $8.00", output, StringComparison.Ordinal);
        Assert.Contains("Owner's changes:", output, StringComparison.Ordinal);
        Assert.Contains("b state Ready → Cancelled: Not needed.", output, StringComparison.Ordinal);
        Assert.Contains("owner-1 added as Ready: added by the owner", output, StringComparison.Ordinal);
    }

    // TASK-08, TASK-07: in a chat, the owner cancels the task an agent works on, which the agent can no longer submit, and the team
    // goes on with the rest. Between replies, /board and /task act on the last message's run: the board's rules and the team's
    // agents and roles hold, every change to a task shows with who, when, what and why, and /report and sof report list the owner's changes.
    [Fact]
    public async Task In_a_chat_the_owner_cancels_a_task_in_progress_and_between_replies_changes_the_last_runs_board()
    {
        sof.Write("sof.json", Team(signOff: false));
        model.When(request => Work(request).StartsWith("You lead a team", StringComparison.Ordinal))
            .CallTools(("create", """{ "id": "a", "title": "Parse", "reason": "plan" }"""), ("create", """{ "id": "b", "title": "Print", "reason": "plan" }"""))
            .Reply("Planned.");
        model.When(request => Work(request).Contains("Do task a,", StringComparison.Ordinal))
            .CallTools(("ask", """{ "question": "Shall I use a regex?" }""")).CallTools(("submit", """{ "id": "a" }""")).Reply("Stopped.");
        model.When(request => Work(request).Contains("Do task b,", StringComparison.Ordinal)).CallTools(("submit", """{ "id": "b" }""")).Reply("Submitted.");
        model.When(request => Work(request).StartsWith("Every task is done", StringComparison.Ordinal)).Reply("Done.");

        var chat = sof.RunAsync("chat", "--agent", "team");
        sof.In.Type("/board");
        await sof.Out.WaitForAsync("error: no message has been sent in this session yet, so there is no board.", Ct);
        sof.In.Type("Write a calculator.");
        await sof.Out.WaitForAsync("#1 developer[1] asks: Shall I use a regex?", Ct);
        sof.In.Type("/task cancel a Changed my mind.");
        await sof.Out.WaitForAsync("note: developer[1] may work on it until its turn ends; /cancel developer[1] stops it now.", Ct);
        sof.In.Type("/answer 1 Yes.");
        await sof.Out.WaitForAsync("team: Completed", Ct);
        var run = sof.Out.ToString().Split('\n').First(line => line.StartsWith("run ", StringComparison.Ordinal))[4..].TrimEnd('\r');

        sof.In.Type("/board");
        await sof.Out.WaitForAsync("b Print: Done, with developer[1], priority 0, $0.00 of $8.00", Ct);
        sof.In.Type("/task cancel b Too late.");
        await sof.Out.WaitForAsync("error: task b cannot go from Done to Cancelled.", Ct);
        sof.In.Type("/task add Document it --role developer");
        await sof.Out.WaitForAsync("so no agent works on this board again; ask for the work in your next message.", Ct);
        sof.In.Type("/task assign owner-1 lead");
        await sof.Out.WaitForAsync("error: there is no agent lead to assign it to. Use one of: developer[1].", Ct);
        sof.In.Type("/task edit owner-1 --role designer");
        await sof.Out.WaitForAsync("error: there is no role designer. Use one of: developer.", Ct);
        sof.In.Type("/task show a");
        await sof.Out.WaitForAsync(" owner: a state InProgress → Cancelled (Changed my mind.)", Ct);
        sof.In.Type("/report");
        await sof.Out.WaitForAsync("owner-1 added as Ready: added by the owner", Ct);
        sof.In.Type("/quit");
        var (exitCode, output, _) = await sof.EndedAsync(chat);
        sof.NewConsole();
        var (_, report, _) = await sof.EndedAsync(sof.RunAsync("report", run));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Changed: owner-1 added as Ready.", output, StringComparison.Ordinal);
        var submitted = model.Requests.Last(request => Work(request).Contains("Do task a,", StringComparison.Ordinal)).History[^1];
        Assert.Contains("task a is not in progress with developer[1].", Assert.IsType<ToolResultContent>(submitted.Content[0]).Text, StringComparison.Ordinal);
        Assert.Contains("a Parse: Cancelled", report, StringComparison.Ordinal);
        Assert.Contains("a state InProgress → Cancelled: Changed my mind.", report, StringComparison.Ordinal);
        Assert.Contains("Task owner-1 (Document it) is Ready.", report, StringComparison.Ordinal);
    }

    // TASK-08, TASK-03, TASK-07: between replies the owner adds, edits, reprioritises, reassigns and cancels tasks of the last message's
    // run; a cycle, a missing dependency or check, an unknown task or agent, and a command it cannot parse are refused with an error. A run
    // that another process holds, or that a rollback left to resume, is refused too, though its board can still be viewed.
    [Fact]
    public async Task Between_replies_every_command_works_and_what_breaks_the_rules_or_cannot_be_applied_safely_is_refused()
    {
        sof.Write("sof.json", """
            {
              "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
              "agents": { "dev": { "instructions": "Work.", "tools": ["planning"] } },
              "tools": { "create": { "source": "builtin:tasks.create" } },
              "toolSets": { "planning": ["create"] },
              "capabilities": { "taskBoard": { "enabled": true }, "conversationStore": { "enabled": true }, "checkpoints": { "enabled": true } }
            }
            """);
        model.CallTools(
                ("create", """{ "id": "t1", "title": "Parse", "reason": "plan" }"""),
                ("create", """{ "id": "t2", "title": "Print", "dependsOn": ["t1"], "reason": "plan" }"""))
            .Reply("Planned.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("Plan it.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        var run = sof.Out.ToString().Split('\n').First(line => line.StartsWith("run ", StringComparison.Ordinal))[4..].TrimEnd('\r');

        sof.In.Type("/task priority t1 high");
        await sof.Out.WaitForAsync("Type /task --help for the syntax.", Ct);
        sof.In.Type("/help task");
        await sof.Out.WaitForAsync("assign <id> <agent>", Ct);
        sof.In.Type("/task add --help");
        await sof.Out.WaitForAsync("--depends <depends>", Ct);
        sof.In.Type("/task add Sum it --depends t2 --priority 2 --criteria One. --criteria Two.");
        await sof.Out.WaitForAsync("Changed: owner-1 added as Proposed.", Ct);
        sof.In.Type("/task edit t1 --depends owner-1");
        await sof.Out.WaitForAsync("error: the dependencies would form a cycle: t1 → owner-1 → t2 → t1.", Ct);
        sof.In.Type("/task edit t1 --depends nope");
        await sof.Out.WaitForAsync("error: task t1 depends on nope, which is not on the board.", Ct);
        sof.In.Type("/task edit t1 --checks nope");
        await sof.Out.WaitForAsync("error: check nope does not exist.", Ct);
        sof.In.Type("/task edit nope --title Other");
        await sof.Out.WaitForAsync("error: the board has no task nope.", Ct);
        sof.In.Type("/task assign t1 nobody");
        await sof.Out.WaitForAsync("error: there is no agent nobody to assign it to. Use one of: dev.", Ct);
        sof.In.Type("""/task edit t2 --title "Print it" --description Pretty. --budget 2 --review true --reason Clearer.""");
        await sof.Out.WaitForAsync("Changed: t2 title Print → Print it, description  → Pretty.", Ct);
        sof.In.Type("/task assign t1 dev");
        await sof.Out.WaitForAsync("Changed: t1 assignee null → dev.", Ct);
        sof.In.Type("/task priority t2 7");
        await sof.Out.WaitForAsync("Changed: t2 priority 0 → 7.", Ct);
        sof.In.Type("/task cancel t1 Not needed.");
        await sof.Out.WaitForAsync("note: t2 depend on t1, so they stay Proposed until you change their --depends or cancel them.", Ct);
        sof.In.Type("/task show owner-1");
        await sof.Out.WaitForAsync("  acceptance criteria: One.; Two.", Ct);
        sof.In.Type("/task show t2");
        await sof.Out.WaitForAsync(" owner: t2 priority 0 → 7 (reprioritised by the owner)", Ct);

        string held;
        await using (new FileStream(Path.Combine(sof.Directory, ".sof", $"{run}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            // Another process holds the run, such as sof resume in another terminal.
            sof.In.Type("/task priority t2 3");
            await sof.Out.WaitForAsync($"error: run {run} is held by another process, so its board is changed there.", Ct);
            sof.In.Type("/board");
            await sof.Out.WaitForAsync("t1 Parse: Cancelled, with dev, priority 0, $0.00 of $8.00", Ct);
            held = sof.Out.ToString().ReplaceLineEndings("\n");
        }

        sof.In.Type($"/rollback {run} --to 0");
        await sof.Out.WaitForAsync($"Run {run} is back at checkpoint 0.", Ct);
        sof.In.Type("/task priority t2 3");
        await sof.Out.WaitForAsync($"error: run {run} stopped without ending, so /resume {run} goes back to its last checkpoint's board", Ct);
        sof.In.Type("/quit");
        var (exitCode, _, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("t2 Print it: Proposed, depends on t1, priority 7, $0.00 of $2.00\nowner-1 Sum it: Proposed, depends on t2, priority 2", held, StringComparison.Ordinal);
        Assert.Contains("  requires a review", held, StringComparison.Ordinal);
        Assert.Contains("(Clearer.)", held, StringComparison.Ordinal);
    }

    /// <summary>A team of a lead and one developer; the lead's plan waits for the owner's sign-off when <paramref name="signOff"/>.</summary>
    private static string Team(bool signOff) => $$"""
        {
          "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
          "agents": {
            "team": { "instructions": "A team.", "pattern": { "type": "team", "lead": "lead", "roles": { "developer": { "max": 1 } } } },
            "lead": { "instructions": "Lead.", "tools": ["planning"] },
            "developer": { "instructions": "Develop.", "tools": ["work"] }
          },
          "tools": {
            "create": { "source": "builtin:tasks.create" },
            "submit": { "source": "builtin:tasks.submit_for_review" },
            "ask": { "source": "builtin:human.ask_owner" }
          },
          "toolSets": { "planning": ["create"], "work": ["submit", "ask"] },
          "capabilities": {
            "taskBoard": { "enabled": true }, "team": { "enabled": true },
            "humanInteraction": { "enabled": true{{(signOff ? """, "signOffs": ["planApproval"]""" : "")}} }
          }
        }
        """;

    /// <summary>The work of a request's current turn, which a conversation's earlier turns come before.</summary>
    private static string Work(ModelRequest request) =>
        string.Concat(request.History[request.TurnStart].Content.OfType<TextContent>().Select(text => text.Text));
}
