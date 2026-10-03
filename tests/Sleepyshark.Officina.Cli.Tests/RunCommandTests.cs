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
        sof.In.Type("approve 7");
        await sof.Out.WaitForAsync("error: nothing waits for you with that number; #1 does.", Ct);
        sof.In.Type("change 1"); // the changed arguments forgotten: refused, and #1 still waits
        await sof.Out.WaitForAsync("error: change needs the call's changed arguments as a JSON object, such as change 3 {\"path\": \"a.txt\"}.", Ct);
        sof.In.Type("approve 1 looks good");
        await sof.Out.WaitForAsync("""approved #1: dev note { "text": "Uses SQLite." }""", Ct);
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

    // RUN-06: Ctrl+C cancels the run as the owner's cancel command does: the run is recorded as cancelled and its report is printed.
    // The signal is the only stand-in; sending a real SIGINT to the test process is not reliable across platforms.
    [Fact]
    public async Task Ctrl_C_cancels_the_run_and_the_report_is_printed()
    {
        using var ctrlC = new CancellationTokenSource();
        sof.Cancel = ctrlC.Token;
        model.CallTools(("note", """{ "text": "Uses SQLite." }"""));

        var run = sof.RunAsync("run", "--input", "Pick a database.");
        await sof.Out.WaitForAsync("#1 dev asks to run note", Ct);
        await ctrlC.CancelAsync();
        var (exitCode, output, _) = await run;

        Assert.Equal(ExitCodes.NotCompleted, exitCode);
        Assert.Contains("dev: HandedOff", output, StringComparison.Ordinal);
        Assert.Contains("\n\nRun ", output, StringComparison.Ordinal);
        sof.Cancel = default;
        var (_, report, _) = await sof.RunAsync("report", output.Split('\n')[0].Split(' ')[1]);
        Assert.Contains("Cancelled", report, StringComparison.OrdinalIgnoreCase);
    }

    // Ctrl+C while the run is still starting ends with one line, not a stack trace.
    [Fact]
    public async Task Ctrl_C_before_the_run_has_started_says_so_and_exits_as_not_completed()
    {
        using var ctrlC = new CancellationTokenSource();
        await ctrlC.CancelAsync();
        sof.Cancel = ctrlC.Token;

        var (exitCode, output, error) = await sof.RunAsync("run", "--input", "Pick a database.");

        Assert.Equal(ExitCodes.NotCompleted, exitCode);
        Assert.Equal("cancelled before the run started.\n", error);
        Assert.DoesNotContain("Run ", output, StringComparison.Ordinal);
    }

    // The process ends this long after Ctrl+C, so the run has its whole run.cancelWithin to stop; System.CommandLine's default is 2 seconds.
    [Fact]
    public void The_process_waits_for_a_cancelled_run_for_as_long_as_the_run_may_take()
    {
        Assert.True(SofCommandLine.TerminationTimeout(new() { Run = new() { CancelWithin = TimeSpan.FromSeconds(30) } }) > TimeSpan.FromSeconds(30));
        Assert.True(SofCommandLine.TerminationTimeout(new()) > new Sleepyshark.Officina.Core.Configuration.RunDefaults().CancelWithin);
    }

    // A number alone is the answer's text, unless a question with that number waits: then the owner left out the answer, which is
    // said each time, and the question keeps waiting.
    [Fact]
    public async Task Answer_with_a_waiting_questions_number_and_no_text_asks_for_the_text()
    {
        model.CallTools(("ask", """{ "question": "How many retries?" }""")).Reply("Done.");

        var run = sof.RunAsync("run", "--input", "Configure it.");
        await sof.Out.WaitForAsync("#1 dev asks: How many retries? Answer with answer 1 <your answer> or deny 1.", Ct);
        sof.In.Type("answer 1");
        sof.In.Type("answer #1");
        await sof.Out.WaitForAsync("error: give your answer after the number: answer 1 <your answer>\nerror: give your answer after the number: answer 1 <your answer>", Ct);
        sof.In.Type("answer 3");
        var (exitCode, output, _) = await run;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("answered #1: dev: How many retries?", output, StringComparison.Ordinal);
        Assert.Contains("3", Assert.IsType<ToolResultContent>(model.Requests[^1].History[^1].Content[0]).Text, StringComparison.Ordinal);
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

    // TEAM-10, UX-01: the plan to sign off lists its tasks indented under the request, and how to answer on a line of its own.
    [Fact]
    public async Task The_plan_to_sign_off_lists_its_tasks_under_the_request()
    {
        sof.Write("sof.json", """
            {
              "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
              "agents": {
                "team": { "instructions": "A team.", "pattern": { "type": "team", "lead": "lead", "roles": { "developer": { "max": 1 } } } },
                "lead": { "instructions": "Lead.", "tools": ["planning"] },
                "developer": { "instructions": "Develop.", "tools": ["work"] }
              },
              "tools": { "create": { "source": "builtin:tasks.create" }, "submit": { "source": "builtin:tasks.submit_for_review" } },
              "toolSets": { "planning": ["create"], "work": ["submit"] },
              "capabilities": {
                "taskBoard": { "enabled": true }, "team": { "enabled": true },
                "humanInteraction": { "enabled": true, "signOffs": ["planApproval"] }
              }
            }
            """);
        model.When(request => Work(request).StartsWith("You lead a team", StringComparison.Ordinal))
            .CallTools(
                ("create", """{ "id": "a", "title": "Parse", "acceptanceCriteria": ["It parses."], "reason": "plan" }"""),
                ("create", """{ "id": "b", "title": "Print", "reason": "plan" }"""))
            .Reply("Planned.");
        model.When(request => Work(request).StartsWith("Every task is done", StringComparison.Ordinal)).Reply("Done.");
        model.When(request => Work(request).Contains("Do task ", StringComparison.Ordinal))
            .CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.").CallTools(("submit", """{ "id": "b" }""")).Reply("Submitted.");

        var run = sof.RunAsync("run", "--agent", "team", "--input", "Write a parser.");
        await sof.Out.WaitForAsync("Answer with approve 1 or deny 1.", Ct);
        sof.In.Type("approve"); // the only request that waits
        var (exitCode, output, _) = await run;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains(
            """
            #1 lead needs your sign-off: Approve the lead's plan before work starts?
              a Parse: Ready
                Acceptance criteria:
                - It parses.
              b Print: Ready
            Answer with approve 1 or deny 1.
            """.ReplaceLineEndings("\n"),
            output,
            StringComparison.Ordinal);
        Assert.Contains("\napproved #1: lead: Approve the lead's plan before work starts?\n", output, StringComparison.Ordinal);
    }

    private static string Work(ModelRequest request) =>
        ScriptedModelProvider.WorkOf(request);

    private static ContentReceived Call(string tool, string arguments) =>
        new(new ToolUseContent("call-note", tool, System.Text.Json.JsonDocument.Parse(arguments).RootElement.Clone()));
}
