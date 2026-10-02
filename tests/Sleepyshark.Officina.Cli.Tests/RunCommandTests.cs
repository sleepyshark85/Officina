using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary><c>sof run</c>: the owner at the console sees each agent's status, what waits for them and the cost, and answers (HITL, UX-01).</summary>
public sealed class RunCommandTests : IDisposable
{
    private const string Configuration = """
        {
          "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
          "agents": { "dev": { "instructions": "Work.", "tools": ["owner"] } },
          "tools": {
            "note": { "source": "builtin:record.propose_finding", "approval": "always" },
            "ask": { "source": "builtin:human.ask_owner" }
          },
          "toolSets": { "owner": ["note", "ask"] },
          "capabilities": { "humanInteraction": { "enabled": true } }
        }
        """;

    private readonly Sof sof = new();
    private readonly ScriptedModelProvider model = new();

    public RunCommandTests()
    {
        sof.Write("sof.json", Configuration);
        sof.Providers["claude"] = model;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => sof.Dispose();

    [Fact]
    public async Task The_owner_sees_the_status_and_answers_approvals_and_questions_and_messages_the_agent()
    {
        model.Reply(Call("note", """{ "text": "Uses SQLite." }"""), new UsageReported(new Usage(1_500_000, 0, 0, 0)), new Stopped(StopReason.WantsTools))
            .CallTools(("ask", """{ "question": "Which database?" }"""))
            .Reply("Done.");

        var run = sof.RunAsync("run", "--input", "Pick a database.");
        await sof.Out.WaitForAsync("#1 dev asks to run note", Ct);
        // The status view applies the run's events as it reads them, which may be just after the request is printed.
        await sof.Out.WaitForAsync("[dev] waits for you: Approval note", Ct);
        sof.In.Type("status");
        await sof.Out.WaitForAsync("cost so far: $1.50", Ct);
        sof.In.Type("approve 1");
        await sof.Out.WaitForAsync("#2 dev asks: Which database?", Ct);
        sof.In.Type("tell dev Keep it short.");
        sof.In.Type("answer 2 Postgres.");
        var (exitCode, output, _) = await run;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("[dev] waits for you: Approval note", output, StringComparison.Ordinal);
        Assert.Contains("dev: waits for you: Approval note\nwaiting for you: #1 dev asks to run note", output, StringComparison.Ordinal);
        Assert.Contains("dev: Completed, cost $1.50\nDone.\n\nRun ", output, StringComparison.Ordinal);
        var last = model.Requests[^1].History;
        Assert.Contains("Postgres.", Assert.IsType<ToolResultContent>(last[^2].Content[0]).Text, StringComparison.Ordinal);
        Assert.Equal(Message.User("<message from=\"owner\">\nKeep it short.\n</message>"), last[^1]);
        Assert.True(File.Exists(Path.Combine(sof.Directory, ".sof", "sof.db")));
    }

    // HITL-05: with nobody at the console the request waits in the queue until its deadline.
    [Fact]
    public async Task With_nobody_answering_a_request_waits_until_its_deadline_and_is_denied()
    {
        model.CallTools(("note", """{ "text": "Uses SQLite." }"""));

        var run = sof.RunAsync("run", "--input", "Pick a database.");
        await sof.Out.WaitForAsync("#1 dev asks to run note", Ct);
        sof.Time.Advance(TimeSpan.FromMinutes(30));
        var (exitCode, output, _) = await run;

        Assert.Equal(ExitCodes.NotCompleted, exitCode);
        Assert.Contains("[dev] working; nobody answered in time", output, StringComparison.Ordinal);
        Assert.Contains("dev: HandedOff (ApprovalDeniedOrTimedOut:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_of_the_wrong_kind_is_refused_and_the_request_keeps_waiting()
    {
        model.CallTools(("note", """{ "text": "Uses SQLite." }""")).Reply("Done.");

        var run = sof.RunAsync("run", "--input", "Pick a database.");
        await sof.Out.WaitForAsync("#1 dev asks to run note", Ct);
        sof.In.Type("answer 1 no, don't");
        await sof.Out.WaitForAsync("error: #1 is not a question.", Ct);
        sof.In.Type("status");
        await sof.Out.WaitForAsync("waiting for you: #1 dev asks to run note", Ct);
        sof.In.Type("approve 1");
        var (exitCode, output, _) = await run;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Done.\n\nRun ", output, StringComparison.Ordinal);
    }

    // TEAM-08, RUN-06, HITL-03: a team's agents show their status by their id; the owner pauses and resumes the whole run, and messages one agent of the team.
    [Fact]
    public async Task The_owner_follows_a_team_pauses_the_run_and_messages_one_of_its_agents()
    {
        sof.Write("sof.json", """
            {
              "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
              "agents": {
                "team": { "instructions": "A team.", "pattern": { "type": "team", "lead": "lead", "roles": { "developer": { "max": 2 } } } },
                "lead": { "instructions": "Lead.", "tools": ["planning"] },
                "developer": { "instructions": "Develop.", "tools": ["work"] }
              },
              "tools": {
                "create": { "source": "builtin:tasks.create" },
                "submit": { "source": "builtin:tasks.submit_for_review" },
                "ask": { "source": "builtin:human.ask_owner" }
              },
              "toolSets": { "planning": ["create"], "work": ["submit", "ask"] },
              "capabilities": { "humanInteraction": { "enabled": true }, "taskBoard": { "enabled": true }, "team": { "enabled": true } }
            }
            """);
        model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith("You lead a team", StringComparison.Ordinal))
            .CallTools(("create", """{ "id": "a", "title": "Parse", "reason": "plan" }""")).Reply("Planned.");
        model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith("Every task is done", StringComparison.Ordinal)).Reply("Parsed.");
        model.When(request => ScriptedModelProvider.WorkOf(request).Contains("Do task a,", StringComparison.Ordinal))
            .CallTools(("ask", """{ "question": "Tabs or spaces?" }""")).CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.");

        var run = sof.RunAsync("run", "--agent", "team", "--input", "Write a parser.");
        await sof.Out.WaitForAsync("#1 developer[1] asks: Tabs or spaces?", Ct);
        sof.In.Type("pause");
        sof.In.Type("tell developer[1] Keep it short.");
        sof.In.Type("tell developer[9] Hello.");
        await sof.Out.WaitForAsync("error: Run ", Ct);
        sof.In.Type("answer 1 Tabs.");
        sof.In.Type("resume");
        var (exitCode, output, _) = await run;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("[developer[1]] working: task:a", output, StringComparison.Ordinal);
        Assert.Contains("[developer[1]] finished", output, StringComparison.Ordinal);
        Assert.Contains("error: Run ", output, StringComparison.Ordinal);
        var developer = model.Requests.Last(request => ScriptedModelProvider.WorkOf(request).Contains("Do task a,", StringComparison.Ordinal)).History;
        Assert.Contains(Message.User("<message from=\"owner\">\nKeep it short.\n</message>"), developer);
        Assert.DoesNotContain(model.Requests.Where(request => !ScriptedModelProvider.WorkOf(request).Contains("Do task a,", StringComparison.Ordinal)),
            request => request.History.Contains(Message.User("<message from=\"owner\">\nKeep it short.\n</message>")));
    }

    // TASK-08, MEM-03, MEM-05: at the console the owner views the run's board, lists the proposed changes to project memory, and decides on them.
    [Fact]
    public async Task The_owner_views_the_board_and_reviews_the_proposed_changes_to_project_memory()
    {
        sof.Write("sof.json", """
            {
              "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
              "run": { "permissionMode": "auto" },
              "agents": { "dev": { "instructions": "Work.", "tools": ["owner"] } },
              "tools": {
                "plan": { "source": "builtin:tasks.create" },
                "propose": { "source": "builtin:memory.propose_change", "gateExemption": "The owner decides on it." },
                "ask": { "source": "builtin:human.ask_owner" }
              },
              "toolSets": { "owner": ["plan", "propose", "ask"] },
              "capabilities": { "humanInteraction": { "enabled": true }, "taskBoard": { "enabled": true }, "projectMemory": { "enabled": true } }
            }
            """);
        model.CallTools(
                ("plan", """{ "id": "t1", "title": "Parse", "reason": "plan" }"""),
                ("propose", """{ "kind": "note", "subject": "build", "text": "Run dotnet test." }"""))
            .CallTools(("ask", """{ "question": "Anything else?" }"""))
            .Reply("Done.");

        var run = sof.RunAsync("run", "--input", "Plan it.");
        await sof.Out.WaitForAsync("#1 dev asks: Anything else?", Ct);
        sof.In.Type("board");
        await sof.Out.WaitForAsync("t1 Parse: Ready, priority 0, $0.00 of $8.00", Ct);
        sof.In.Type("memory");
        await sof.Out.WaitForAsync("#1 note build: Run dotnet test. (by dev)", Ct);
        sof.In.Type("memory approve 1 Agreed.");
        await sof.Out.WaitForAsync("Approved #1.", Ct);
        sof.In.Type("memory");
        await sof.Out.WaitForAsync("no changes to project memory wait for you.", Ct);
        sof.In.Type("answer 1 No.");
        var (exitCode, _, _) = await run;

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task A_provider_this_build_does_not_have_is_reported()
    {
        sof.Write("sof.json", """
            {
              "providers": { "other": { "prices": { "m": { "input": 1 } } } },
              "models": { "default": { "provider": "other", "model": "m" } },
              "agents": { "dev": { "instructions": "Work." } }
            }
            """);

        var (exitCode, _, error) = await sof.RunAsync("run", "--input", "Go.");

        Assert.Equal((ExitCodes.Usage, "error: provider \"other\" is not available in this build of sof.\n"), (exitCode, error));
    }

    private static ContentReceived Call(string tool, string arguments) =>
        new(new ToolUseContent("call-note", tool, System.Text.Json.JsonDocument.Parse(arguments).RootElement.Clone()));
}
