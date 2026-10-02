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
        Assert.Contains("dev: Completed, cost $1.50\nDone.\n", output, StringComparison.Ordinal);
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
        Assert.Contains("Done.\n", output, StringComparison.Ordinal);
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
