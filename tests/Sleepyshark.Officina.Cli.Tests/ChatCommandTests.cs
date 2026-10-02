using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// <c>sof chat</c>: a session that stays open, where each line is a message in the conversation the agent keeps with the owner
/// (TRG-01, CAP-05), the reply streams (LAT-02), and the console's commands work as in <c>sof run</c> (HITL, RUN-06, UX-01).
/// </summary>
public sealed class ChatCommandTests : IDisposable
{
    // The conversation store is not set: sof chat turns it on, with full history for the agent.
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

    public ChatCommandTests()
    {
        sof.Write("sof.json", Configuration);
        sof.Providers["claude"] = model;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => sof.Dispose();

    // LAT-02: the first text reaches the console while the model is still writing, and while the console read waits for the
    // owner, as a read from a real terminal blocks its thread (the stand-in console does the same).
    [Fact]
    public async Task The_reply_streams_while_the_console_waits_for_the_owner()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sof.Providers["claude"] = new HeldModel(model, release);
        model.Reply(new TextDelta("Hel"), new TextDelta("lo."), new Stopped(StopReason.Finished));

        var chat = sof.RunAsync("chat");
        await sof.Out.WaitForAsync("Chatting with dev.", Ct);
        sof.In.Type("Say hello.");
        await sof.Out.WaitForAsync("[dev] Hel", Ct);
        Assert.DoesNotContain("lo.", sof.Out.ToString(), StringComparison.Ordinal);
        release.SetResult();
        await sof.Out.WaitForAsync("dev: Completed, cost $0.00; this session $0.00", Ct);
        sof.In.Type("/quit");
        var (exitCode, output, _) = await chat;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("[dev] Hello.\n", output, StringComparison.Ordinal);
        Assert.Contains("The session has ended. It cost $0.00.", output, StringComparison.Ordinal);
    }

    // TRG-01, CAP-05: each message is a run of its own in one conversation, which goes on in the next session; --new starts another.
    [Fact]
    public async Task The_conversation_carries_over_from_message_to_message_and_session_to_session_until_new()
    {
        model.Reply("Hi Ann.").Reply("Ann.").Reply("Still Ann.").Reply("Who?");

        var chat = sof.RunAsync("chat");
        sof.In.Type("My name is Ann.");
        sof.In.Type("What is my name?");
        sof.In.Dispose(); // Ctrl+D: the session ends once both messages have their replies
        var (exitCode, output, _) = await chat;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(2, output.Split('\n').Count(line => line.StartsWith("run ", StringComparison.Ordinal)));
        Assert.Equal(
            [Message.User("My name is Ann."), Message.Assistant("Hi Ann."), Message.User("What is my name?")], model.Requests[1].History);

        sof.NewConsole();
        chat = sof.RunAsync("chat");
        sof.In.Type("And now?");
        await sof.Out.WaitForAsync("[dev] Still Ann.", Ct);
        sof.In.Dispose(); // Ctrl+D
        Assert.Equal(ExitCodes.Success, (await chat).ExitCode);
        Assert.Equal(5, model.Requests[2].History.Length);

        sof.NewConsole();
        chat = sof.RunAsync("chat", "--new");
        sof.In.Type("And now?");
        await sof.Out.WaitForAsync("[dev] Who?", Ct);
        sof.In.Dispose();
        Assert.Equal(ExitCodes.Success, (await chat).ExitCode);
        Assert.Equal([Message.User("And now?")], model.Requests[3].History);
    }

    // HITL-02, HITL-03, UX-01: while a reply runs, the owner's commands work as in sof run, and a message waits until the reply ends.
    [Fact]
    public async Task While_a_reply_runs_commands_are_carried_out_and_a_message_waits_for_it()
    {
        model.CallTools(("note", """{ "text": "Uses SQLite." }""")).Reply("Noted.").Reply("Next.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("Note the database.");
        await sof.Out.WaitForAsync("#1 dev asks to run note", Ct);
        Assert.Contains("Answer with /approve, /deny or /change.", sof.Out.ToString(), StringComparison.Ordinal);
        sof.In.Type("And then the next thing.");
        await sof.Out.WaitForAsync("(it is sent when this reply ends", Ct);
        sof.In.Type("/tell dev Keep it short.");
        sof.In.Type("/status");
        await sof.Out.WaitForAsync("waiting for you: #1 dev asks to run note", Ct);
        sof.In.Type("approve 1"); // without the slash it is a message, which waits too
        sof.In.Type("/approve 1");
        await sof.Out.WaitForAsync("[dev] Next.", Ct);
        sof.In.Type("/quit");
        var (exitCode, output, _) = await chat;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("dev: Completed, cost $0.00; this session $0.00", output, StringComparison.Ordinal);
        Assert.Contains(Message.User("<message from=\"owner\">\nKeep it short.\n</message>"), model.Requests[1].History);
        Assert.Equal(Message.User("And then the next thing."), model.Requests[2].History[^1]);
        Assert.Equal(3, model.Requests.Count); // "approve 1" waited behind the first message, and the session ended before it was sent
    }

    // RUN-06: Ctrl+C cancels the reply, drops the messages that waited for it, and the session goes on; a second Ctrl+C ends it.
    [Fact]
    public async Task Ctrl_C_cancels_the_reply_and_keeps_the_session_and_a_second_one_ends_it()
    {
        model.CallTools(("note", """{ "text": "Uses SQLite." }""")).Reply("Hi.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("Note the database.");
        await sof.Out.WaitForAsync("#1 dev asks to run note", Ct);
        sof.In.Type("This waits.");
        await sof.Out.WaitForAsync("(it is sent when this reply ends", Ct);
        sof.Press(PosixSignal.SIGINT);
        await sof.Out.WaitForAsync("the 1 message(s) that waited for it are dropped.", Ct);
        sof.In.Type("Hello again.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.Press(PosixSignal.SIGINT);
        await sof.Out.WaitForAsync("Press Ctrl+C again, or type /quit, to end the session.", Ct);
        sof.Press(PosixSignal.SIGINT);
        var (exitCode, output, _) = await chat;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("dev: HandedOff (", output, StringComparison.Ordinal);
        Assert.Equal(Message.User("Hello again."), model.Requests[^1].History[^1]);
        var cancelled = output.Split('\n').First(line => line.StartsWith("run ", StringComparison.Ordinal))[4..];
        sof.NewConsole();
        var (_, report, _) = await sof.RunAsync("report", cancelled);
        Assert.Contains("Cancelled", report, StringComparison.OrdinalIgnoreCase);
    }

    // SIGTERM, or the command line's cancellation, ends the session at once, and the reply that runs is cancelled cleanly.
    [Fact]
    public async Task SIGTERM_ends_the_session_and_cancels_the_reply()
    {
        model.CallTools(("note", """{ "text": "Uses SQLite." }"""));

        var chat = sof.RunAsync("chat");
        sof.In.Type("Note the database.");
        await sof.Out.WaitForAsync("#1 dev asks to run note", Ct);
        sof.Press(PosixSignal.SIGTERM);
        var (exitCode, output, _) = await chat;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("dev: HandedOff (", output, StringComparison.Ordinal);
        Assert.Contains("The session has ended.", output, StringComparison.Ordinal);
    }

    // The end of the input ends the session once the messages sent before it have their replies, so a script can be piped in.
    [Fact]
    public async Task The_end_of_the_input_ends_the_session_after_the_replies()
    {
        model.Reply("One.").Reply("Two.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("First.");
        sof.In.Type("/status");
        sof.In.Type("Second.");
        sof.In.Dispose();
        var (exitCode, output, _) = await chat;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(output.IndexOf("[dev] Two.", StringComparison.Ordinal) < output.IndexOf("The session has ended.", StringComparison.Ordinal));
        Assert.Equal(2, model.Requests.Count);
    }

    // HITL-01: between replies only the commands that need no run work; /mode holds for the next replies, and /report shows the last one's run.
    [Fact]
    public async Task Between_replies_the_mode_holds_for_the_next_reply_and_the_report_is_the_last_ones()
    {
        sof.Write("sof.json", """
            {
              "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
              "agents": { "dev": { "instructions": "Work.", "tools": ["owner"] } },
              "tools": { "remember": { "source": "builtin:record.propose_finding", "kind": "write", "gateExemption": "Tests only." } },
              "toolSets": { "owner": ["remember"] },
              "capabilities": { "humanInteraction": { "enabled": true } }
            }
            """);
        model.CallTools(("remember", """{ "text": "Uses SQLite." }""")).Reply("Noted.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("/approve 1");
        await sof.Out.WaitForAsync("error: no reply is running; /approve works while one does.", Ct);
        sof.In.Type("/report");
        await sof.Out.WaitForAsync("error: no message has been sent in this session yet.", Ct);
        sof.In.Type("/mode auto"); // in ask mode, the write would wait for the owner's approval
        sof.In.Type("Note the database.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("/report");
        await sof.Out.WaitForAsync("Work: Note the database.", Ct);
        sof.In.Type("/quit");
        var (exitCode, output, _) = await chat;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("asks to run", output, StringComparison.Ordinal);
    }

    // CAP-05: chatting would override a conversation store the configuration turns off, so sof chat refuses, and config validate says so.
    [Fact]
    public async Task A_conversation_store_the_configuration_turns_off_is_refused_and_validate_says_so()
    {
        sof.Write("sof.json", """
            {
              "agents": { "dev": { "instructions": "Work." }, "batch": { "instructions": "Work.", "triggers": ["batch"] } },
              "capabilities": { "conversationStore": { "enabled": false } }
            }
            """);

        var (exitCode, _, error) = await sof.RunAsync("chat", "--agent", "dev");
        var (valid, notes, _) = await sof.RunAsync("config", "validate");

        Assert.Equal(ExitCodes.Invalid, exitCode);
        Assert.Equal("error: sof chat keeps the conversation in the conversation store, which capabilities.conversationStore.enabled turns off. Remove that setting, or set it to true.\n", error);
        Assert.Equal(ExitCodes.Success, valid);
        Assert.Equal(
            "note: sof chat refuses this: sof chat keeps the conversation in the conversation store, which capabilities.conversationStore.enabled turns off. Remove that setting, or set it to true.\nThe configuration is valid.\n",
            notes);
    }

    [Fact]
    public async Task An_agent_whose_triggers_leave_out_conversation_is_refused_and_validate_says_so()
    {
        sof.Write("sof.json", """{ "agents": { "dev": { "instructions": "Work." }, "batch": { "instructions": "Work.", "triggers": ["batch"] } } }""");

        var (exitCode, _, error) = await sof.RunAsync("chat", "--agent", "batch");
        var (_, notes, _) = await sof.RunAsync("config", "validate");

        Assert.Equal(ExitCodes.Invalid, exitCode);
        Assert.Contains("agent \"batch\" takes no conversations: agents.batch.triggers leaves out conversation.", error, StringComparison.Ordinal);
        Assert.Contains("note: sof chat refuses this: agent \"batch\" takes no conversations", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("\"dev\"", notes, StringComparison.Ordinal);
    }

    // CTX-06: a history strategy the configuration sets is kept; with none, each message starts a new conversation.
    [Fact]
    public async Task A_history_strategy_the_configuration_sets_is_kept()
    {
        sof.Write("sof.json", """
            {
              "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
              "agents": { "dev": { "instructions": "Work.", "context": { "history": { "strategy": "none" } } } }
            }
            """);
        model.Reply("One.").Reply("Two.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("First.");
        sof.In.Type("Second.");
        sof.In.Dispose();
        var (exitCode, output, _) = await chat;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("note: agents.dev.context.history.strategy is none, so each message starts a new conversation.", output, StringComparison.Ordinal);
        Assert.Equal([Message.User("Second.")], model.Requests[1].History);
    }

    // TEAM, CAP-05: each message is new work for the team, and its lead keeps the conversation, so it plans with the earlier goals in mind.
    [Fact]
    public async Task Each_message_is_new_work_for_the_team_whose_lead_remembers_the_earlier_goals()
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
              "capabilities": { "taskBoard": { "enabled": true }, "team": { "enabled": true } }
            }
            """);
        model.When(request => Work(request).StartsWith("You lead a team", StringComparison.Ordinal))
            .CallTools(("create", """{ "id": "a", "title": "Parse", "reason": "plan" }""")).Reply("Planned.")
            .CallTools(("create", """{ "id": "b", "title": "Print", "reason": "plan" }""")).Reply("Planned.");
        model.When(request => Work(request).StartsWith("Every task is done", StringComparison.Ordinal)).Reply("Parsed.").Reply("Printed.");
        model.When(request => Work(request).Contains("Do task ", StringComparison.Ordinal))
            .CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.").CallTools(("submit", """{ "id": "b" }""")).Reply("Submitted.");

        var chat = sof.RunAsync("chat", "--agent", "team");
        sof.In.Type("Write a parser.");
        sof.In.Type("Now print the result.");
        sof.In.Dispose();
        var (exitCode, output, _) = await chat;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("[lead] Printed.", output, StringComparison.Ordinal);
        var secondPlan = model.Requests.Last(request => Work(request).StartsWith("You lead a team", StringComparison.Ordinal)).History;
        Assert.Contains(secondPlan, message => message.Content.OfType<TextContent>().Any(text => text.Text.Contains("Write a parser.", StringComparison.Ordinal)));
        Assert.Contains(secondPlan, message => message.Content.OfType<TextContent>().Any(text => text.Text.Contains("Now print the result.", StringComparison.Ordinal)));
        var developer = model.Requests.Last(request => Work(request).Contains("Do task b", StringComparison.Ordinal)).History;
        Assert.DoesNotContain(developer, message => message.Content.OfType<TextContent>().Any(text => text.Text.Contains("Do task a", StringComparison.Ordinal)));
    }

    // RUN-04, RUN-08: each message's run is checkpointed, rolled back and resumed on its own. A rollback that would remove a later
    // message's turns is refused, and a resumed message runs with the conversation, as sof chat ran it.
    [Fact]
    public async Task A_message_s_run_rolls_back_and_resumes_with_the_conversation()
    {
        sof.Write("sof.json", """
            {
              "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
              "agents": { "dev": { "instructions": "Work." } },
              "capabilities": { "conversationStore": { "enabled": true }, "checkpoints": { "enabled": true } }
            }
            """);
        model.Reply("One.").Reply("Two.").Reply("Two again.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("First.");
        sof.In.Type("Second.");
        sof.In.Dispose();
        var (_, output, _) = await chat;
        var runs = output.Split('\n').Where(line => line.StartsWith("run ", StringComparison.Ordinal)).Select(line => line[4..]).ToList();

        sof.NewConsole();
        var (refused, _, error) = await sof.RunAsync("rollback", runs[0], "--to", "0");
        var (rolledBack, _, _) = await sof.RunAsync("rollback", runs[1], "--to", "0");
        var (resumed, _, _) = await sof.RunAsync("resume", runs[1]);

        Assert.Equal(ExitCodes.Invalid, refused);
        Assert.Contains("Another run has written to dev's conversation", error, StringComparison.Ordinal);
        Assert.Equal((ExitCodes.Success, ExitCodes.Success), (rolledBack, resumed));
        Assert.Equal([Message.User("First."), Message.Assistant("One."), Message.User("Second.")], model.Requests[2].History);
    }

    /// <summary>The work of a request's current turn, which a conversation's earlier turns come before.</summary>
    private static string Work(ModelRequest request) =>
        string.Concat(request.History[request.TurnStart].Content.OfType<TextContent>().Select(text => text.Text));

    /// <summary>A model that holds its reply after the first piece of text until it is released.</summary>
    private sealed class HeldModel(ScriptedModelProvider inner, TaskCompletionSource release) : IModelProvider
    {
        public ProviderCapabilities CapabilitiesOf(string model) => inner.CapabilitiesOf(model);

        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var held = false;
            await foreach (var modelEvent in inner.StreamAsync(request, ct))
            {
                yield return modelEvent;
                if (modelEvent is TextDelta && !held)
                {
                    held = true;
                    await release.Task.WaitAsync(ct);
                }
            }
        }
    }
}
