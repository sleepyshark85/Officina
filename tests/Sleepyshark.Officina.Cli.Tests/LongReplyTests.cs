using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// Long replies in a chat session at a terminal: a reply of more than 20 lines shows its first 12 and says how much more there
/// is; <c>/show</c> opens a reply in a pager, <c>/history</c> lists them, and <c>/save n</c> saves one. Requests that wait for the
/// owner are never folded. Only the console, the signals, the model, the clock and the pager are stand-ins.
/// </summary>
public sealed class LongReplyTests : IDisposable
{
    private const string Configuration = """
        {
          "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
          "agents": { "dev": { "instructions": "Work." } }
        }
        """;

    private readonly Sof sof = new();
    private readonly ScriptedModelProvider model = new();

    public LongReplyTests()
    {
        sof.Write("sof.json", Configuration);
        sof.Providers["claude"] = model;
        sof.Variables["PAGER"] = "test-pager";
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>$PAGER, as the shell runs it.</summary>
    private static string TestPager => OperatingSystem.IsWindows() ? "cmd.exe /d /s /c \"test-pager\"" : "/bin/sh -c \"test-pager\"";

    public void Dispose() => sof.Dispose();

    private static string Command(string program, string arguments) => $"{program} {arguments}".TrimEnd();

    private static string? Command((string Program, string Arguments)? pager) => pager is var (program, arguments) ? Command(program, arguments) : null;

    // A reply of 20 lines prints in full; one of 21 shows its first 12, and says how many more lines there are, their size,
    // and how to read and keep them.
    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public async Task A_reply_of_more_than_20_lines_is_folded_after_its_first_12(int lines, bool folded)
    {
        sof.Interactive = true;
        var text = Points(lines);
        model.Reply(Streamed(text));

        var chat = sof.RunAsync("chat");
        sof.In.Type("List the points.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("/quit");
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("[dev] Point 1.\nPoint 2.\n", output, StringComparison.Ordinal);
        Assert.Contains("Point 12.\n", output, StringComparison.Ordinal);
        Assert.Equal(!folded, output.Contains("Point 13.", StringComparison.Ordinal));
        Assert.Equal(!folded, output.Contains($"Point {lines}.", StringComparison.Ordinal));
        if (folded)
        {
            Assert.Contains("Point 12.\n[dev] … (folded; /show to read)\n", output, StringComparison.Ordinal);
            Assert.Contains("[dev] … 9 more lines\n", output, StringComparison.Ordinal);
            Assert.Contains($"… 9 more lines ({Encoding.UTF8.GetByteCount(text)} bytes). /show to read all · /save to keep it\n", output, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("/show", output, StringComparison.Ordinal);
            Assert.DoesNotContain("more lines\n", output, StringComparison.Ordinal);
        }
    }

    // A short reply is unchanged: no count, no summary.
    [Fact]
    public async Task A_short_reply_is_not_folded()
    {
        sof.Interactive = true;
        model.Reply("Hi.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("Say hi.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("/quit");
        var (_, output, _) = await sof.EndedAsync(chat);

        Assert.Contains("[dev] Hi.\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("…", output, StringComparison.Ordinal);
    }

    // A long reply saved when the session offers it says where it was saved.
    [Fact]
    public async Task The_summary_of_a_saved_reply_names_the_file()
    {
        sof.Interactive = true;
        var text = Points(45);
        model.Reply(Streamed(text));

        var chat = sof.RunAsync("chat");
        sof.In.Type("List the points.");
        await sof.Out.WaitForAsync("Save this as docs/list-the-points.md?", Ct);
        sof.In.Type("");
        await sof.Out.WaitForAsync("Saved docs/list-the-points.md. It is not committed.", Ct);
        await sof.Out.WaitForAsync("/show to read all", Ct);
        sof.In.Type("/quit");
        var (_, output, _) = await sof.EndedAsync(chat);

        Assert.Contains($"… 33 more lines ({Encoding.UTF8.GetByteCount(text)} bytes), saved as docs/list-the-points.md. /show to read all\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/save to keep it", output, StringComparison.Ordinal);
    }

    // /show opens the last reply in the pager, $PAGER run by the shell, and /show n reply n.
    [Fact]
    public async Task Show_opens_the_last_reply_or_a_numbered_one_in_the_pager()
    {
        sof.Interactive = true;
        var points = Points(25);
        model.Reply(Streamed(points)).Reply("Hi.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("List the points.");
        sof.In.Type("Say hi.");
        await sof.Out.WaitForAsync("dev: Completed", 2, Ct);
        sof.In.Type("/show");
        sof.In.Type("/show 1");
        sof.In.Type("/show 2");
        sof.In.Type("/show 3");
        sof.In.Type("/show #2");
        sof.In.Type("/quit");
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(3, sof.Foregrounded.Count);
        Assert.All(sof.Foregrounded, paged => Assert.Equal(TestPager, Command(paged.Program, paged.Arguments)));
        Assert.Equal(["Hi.", points, "Hi."], sof.Foregrounded.Select(paged => paged.Text.Trim()));
        Assert.Contains("error: there is no reply 3; /history lists the session's replies.", output, StringComparison.Ordinal);
        Assert.Contains("error: /show takes a reply's number, such as /show 2; /history lists them.", output, StringComparison.Ordinal);
    }

    // /history lists each reply with its agent, run, title, size and where it was saved; /save n saves reply n.
    [Fact]
    public async Task History_lists_the_replies_and_save_takes_a_reply_s_number()
    {
        sof.Interactive = true;
        var requirements = "# Login requirements\n\n" + Points(44);
        model.Reply(Streamed(requirements)).Reply("Hi.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("/history");
        await sof.Out.WaitForAsync("No reply yet in this session.", Ct);
        sof.In.Type("Write the login requirements.");
        await sof.Out.WaitForAsync("Save this as docs/login-requirements.md?", Ct);
        sof.In.Type("");
        await sof.Out.WaitForAsync("saved as docs/login-requirements.md. /show to read all", Ct);
        sof.In.Type("Say hi.");
        await sof.Out.WaitForAsync("dev: Completed", 2, Ct);
        sof.In.Type("/history");
        sof.In.Type("/save 2 notes/hi.md");
        await sof.Out.WaitForAsync("Saved notes/hi.md.", Ct);
        sof.In.Type("/save 1");
        await sof.Out.WaitForAsync("Saved docs/login-requirements-2.md.", Ct);
        sof.In.Type("/history");
        sof.In.Type("/quit");
        var (_, output, _) = await sof.EndedAsync(chat);

        var runs = output.Split('\n').Where(line => line.StartsWith("run ", StringComparison.Ordinal)).Select(line => "…" + line[^8..]).ToList();
        var size = $"{Encoding.UTF8.GetByteCount(requirements)} bytes";
        Assert.Contains(
            $"1. dev, run {runs[0]}: Login requirements (46 lines, {size}; saved as docs/login-requirements.md)\n2. dev, run {runs[1]}: Hi. (1 line, 3 bytes)\n",
            output, StringComparison.Ordinal);
        Assert.Contains(
            $"1. dev, run {runs[0]}: Login requirements (46 lines, {size}; saved as docs/login-requirements-2.md)\n2. dev, run {runs[1]}: Hi. (1 line, 3 bytes; saved as notes/hi.md)\n",
            output, StringComparison.Ordinal);
        Assert.Equal("Hi.\n", File.ReadAllText(Path.Combine(sof.Directory, "notes", "hi.md")));
    }

    // While a reply runs, /show opens its text so far, as does /show with its number; /show n an earlier reply, and /history
    // lists the one that runs.
    [Fact]
    public async Task While_a_reply_runs_show_opens_its_text_so_far()
    {
        sof.Interactive = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sof.Providers["claude"] = new StallingModel(model, 2, release.Task, stalled);
        model.Reply("Hi.").Reply(Streamed(Points(3)));

        var chat = sof.RunAsync("chat");
        sof.In.Type("Say hi.");
        await sof.Out.WaitForAsync("[dev] Hi.", Ct);
        sof.In.Type("List the points.");
        await stalled.Task.WaitAsync(Ct);
        sof.In.Type("/show");
        sof.In.Type("/show 2");
        sof.In.Type("/show 1");
        sof.In.Type("/history");
        await sof.Out.WaitForAsync(": running", Ct);
        release.SetResult();
        await sof.Out.WaitForAsync("dev: Completed", 2, Ct);
        sof.In.Type("/quit");
        var (_, output, _) = await sof.EndedAsync(chat);

        Assert.Equal([Points(3), Points(3), "Hi."], sof.Foregrounded.Select(paged => paged.Text.Trim()));
        Assert.Matches(@"\n2\. dev, run …[0-9a-f]{8}: running\n", output);
    }

    // An agent's folded text before it asks the owner, such as a warning about the write it asks to make, is printed before
    // the request, so the owner sees it before deciding. A reply's text is every model call's: /show opens all of it, while
    // the request waits and after the reply.
    [Fact]
    public async Task Folded_text_is_printed_before_the_owner_is_asked_and_the_reply_keeps_every_call_s_text()
    {
        sof.Interactive = true;
        sof.Write("sof.json", """
            {
              "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } },
              "agents": { "dev": { "instructions": "Work.", "tools": ["owner"] } },
              "tools": { "write_file": { "source": "builtin:record.propose_finding", "approval": "always" } },
              "toolSets": { "owner": ["write_file"] },
              "capabilities": { "humanInteraction": { "enabled": true } }
            }
            """);
        var warning = "IMPORTANT: this write will also wipe your notes.txt. Approve only if that is fine.";
        var text = string.Join("\n", Enumerable.Range(1, 29).Select(number => number == 25 ? warning : $"Step {number}."));
        model.Reply([
            .. Streamed(text).SkipLast(1),
            new ContentReceived(new ToolUseContent("call-1", "write_file", System.Text.Json.JsonDocument.Parse("""{ "text": "notes" }""").RootElement)),
            new Stopped(StopReason.WantsTools)]).Reply("Done.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("Write the file.");
        await sof.Out.WaitForAsync("#1 dev asks to run write_file", Ct);
        sof.In.Type("/show");
        sof.In.Type("/approve 1");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("/show");
        sof.In.Type("/quit");
        var (_, output, _) = await sof.EndedAsync(chat);

        Assert.True(output.IndexOf(warning, StringComparison.Ordinal) < output.IndexOf("#1 dev asks to run write_file", StringComparison.Ordinal));
        Assert.Contains("[dev] … the folded lines, as you are asked:\n[dev] Step 13.\n", output, StringComparison.Ordinal);
        Assert.Equal([text, $"{text}\n\nDone."], sof.Foregrounded.Select(paged => paged.Text.Trim()));
        Assert.DoesNotContain("/show to read all", output, StringComparison.Ordinal); // nothing is left folded
    }

    // With piped input, or output that is not a terminal, every reply prints in full, and /show prints, without a pager.
    [Fact]
    public async Task Without_a_terminal_replies_print_in_full_and_show_prints()
    {
        var points = Points(30);
        model.Reply(Streamed(points));

        var chat = sof.RunAsync("chat");
        sof.In.Type("List the points.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("/show");
        sof.In.Dispose();
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains($"[dev] {points}", output, StringComparison.Ordinal);
        Assert.Equal(2, output.Split('\n').Count(line => line == "Point 30."));
        Assert.DoesNotContain("more line", output, StringComparison.Ordinal);
        Assert.Empty(sof.Foregrounded);
    }

    // A pager that cannot be started, or fails, such as a $PAGER that is not a command (127 from the shell), leaves the reply
    // printed, and says so.
    [Theory]
    [InlineData(null, "could not be started")]
    [InlineData(127, "ended with exit code 127")]
    public async Task A_pager_that_cannot_be_started_or_fails_leaves_the_reply_printed(int? exitCode, string why)
    {
        sof.Interactive = true;
        sof.Foreground = (_, _, _, _) => Task.FromResult(exitCode);
        var points = Points(25);
        model.Reply(Streamed(points));

        var chat = sof.RunAsync("chat");
        sof.In.Type("List the points.");
        await sof.Out.WaitForAsync("/show to read all", Ct);
        sof.In.Type("/show");
        await sof.Out.WaitForAsync(why, Ct);
        sof.In.Type("/quit");
        var (_, output, _) = await sof.EndedAsync(chat);

        Assert.Contains($"note: the pager, {TestPager.Split(" ")[0]}, {why}, so the reply is printed.\n{points}", output, StringComparison.Ordinal);
    }

    // While the pager has the terminal, Ctrl+C is the pager's: the session neither cancels anything nor counts it.
    [Fact]
    public async Task Ctrl_C_while_the_pager_runs_is_the_pager_s()
    {
        sof.Interactive = true;
        var paging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sof.Foreground = async (_, _, _, _) =>
        {
            paging.SetResult();
            await quit.Task;
            return 0;
        };
        model.Reply("Hi.").Reply("Again.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("Say hi.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("/show");
        await paging.Task.WaitAsync(Ct);
        sof.Press(PosixSignal.SIGINT);
        sof.Press(PosixSignal.SIGINT);
        quit.SetResult();
        sof.In.Type("Say it again.");
        await sof.Out.WaitForAsync("[dev] Again.", Ct);
        sof.In.Type("/quit");
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("Ctrl+C", output, StringComparison.Ordinal);
    }

    // SIGTERM while the pager runs ends the session: the pager is stopped first.
    [Fact]
    public async Task SIGTERM_while_the_pager_runs_stops_it_and_ends_the_session()
    {
        sof.Interactive = true;
        var paging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        sof.Foreground = async (_, _, _, ct) =>
        {
            paging.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                stopped = true;
            }

            return 0;
        };
        model.Reply("Hi.");

        var chat = sof.RunAsync("chat");
        sof.In.Type("Say hi.");
        await sof.Out.WaitForAsync("dev: Completed", Ct);
        sof.In.Type("/show");
        await paging.Task.WaitAsync(Ct);
        sof.Press(PosixSignal.SIGTERM);
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(stopped);
        Assert.Contains("The session has ended.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("note: the pager", output, StringComparison.Ordinal);
    }

    // Ctrl+C while a folded reply streams cancels it as ever; what it had written is the reply /show opens.
    [Fact]
    public async Task Ctrl_C_during_a_folded_reply_cancels_it_and_show_opens_what_was_written()
    {
        sof.Interactive = true;
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sof.Providers["claude"] = new StallingModel(model, 1, Task.Delay(Timeout.Infinite, Ct), stalled);
        var points = Points(30);
        model.Reply(Streamed(points));

        var chat = sof.RunAsync("chat");
        sof.In.Type("List the points.");
        await sof.Out.WaitForAsync("[dev] … (folded; /show to read)", Ct);
        await stalled.Task.WaitAsync(Ct);
        sof.Press(PosixSignal.SIGINT);
        await sof.Out.WaitForAsync("/show to read all", Ct);
        sof.In.Type("/show");
        sof.In.Type("/quit");
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Cancelling. Press Ctrl+C again to end the session.", output, StringComparison.Ordinal);
        Assert.Contains("dev: HandedOff (", output, StringComparison.Ordinal);
        Assert.Contains("[dev] … 18 more lines\n", output, StringComparison.Ordinal);
        Assert.Contains("… 18 more lines (", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Point 13.", output, StringComparison.Ordinal);
        Assert.Equal(points, Assert.Single(sof.Foregrounded).Text.Trim());
    }

    // A sign-off waits for the owner, who needs all of it to decide: a long plan prints in full. The lead's report, the team's
    // reply, follows the rule of any reply.
    [Fact]
    public async Task A_sign_off_is_never_folded_and_the_lead_s_report_is_folded_when_long()
    {
        sof.Interactive = true;
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
        var criteria = string.Join(", ", Enumerable.Range(1, 30).Select(number => $"\"Criterion {number} holds.\""));
        var report = "Report.\n" + Points(29);
        model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith("You lead a team", StringComparison.Ordinal))
            .CallTools(("create", $$"""{ "id": "a", "title": "Parse", "acceptanceCriteria": [{{criteria}}], "reason": "plan" }"""))
            .Reply("Planned.");
        model.When(request => Given(request, "Every task is done")).Reply(Streamed(report));
        model.When(request => ScriptedModelProvider.WorkOf(request).Contains("Do task ", StringComparison.Ordinal))
            .CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.");

        var chat = sof.RunAsync("chat", "--agent", "team");
        sof.In.Type("Write a parser.");
        await sof.Out.WaitForAsync("Answer with /approve 1 or /deny 1.", Ct);
        sof.In.Type("/approve 1");
        await sof.Out.WaitForAsync("Save this as docs/plans/", Ct);
        sof.In.Type("n");
        await sof.Out.WaitForAsync("team: Completed", Ct);
        await sof.Out.WaitForAsync("/show to read all", Ct);
        sof.In.Type("/show");
        sof.In.Type("/quit");
        var (exitCode, output, _) = await sof.EndedAsync(chat);

        Assert.Equal(ExitCodes.Success, exitCode);
        var signOff = output[output.IndexOf("#1 lead needs your sign-off", StringComparison.Ordinal)..];
        Assert.All(Enumerable.Range(1, 30), number => Assert.Contains($"  - Criterion {number} holds.\n", signOff, StringComparison.Ordinal));
        Assert.Contains("  - Criterion 30 holds.\nAnswer with /approve 1 or /deny 1.\n", signOff, StringComparison.Ordinal);
        Assert.Contains("Point 11.\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Point 12.", output, StringComparison.Ordinal);
        Assert.Contains("… 18 more lines (", output, StringComparison.Ordinal);
        var shown = Assert.Single(sof.Foregrounded).Text.Trim();
        Assert.StartsWith("[lead] Planned.\n\n", shown, StringComparison.Ordinal); // every call's text, each after its agent's name
        Assert.Contains("[developer[1]] Submitted.", shown, StringComparison.Ordinal);
        Assert.EndsWith($"[lead] {report}", shown, StringComparison.Ordinal);
    }

    // $PAGER first, run by the shell; else less -R when it is on the path; else more on Windows, and on other systems none.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_pager_is_PAGER_or_less_or_more_on_Windows(bool windows)
    {
        var folder = Path.Combine(sof.Directory, "bin");
        Directory.CreateDirectory(folder);
        var less = Path.Combine(folder, windows ? "less.exe" : "less");
        File.WriteAllText(less, "");

        Assert.Equal(
            windows ? "cmd.exe /d /s /c \"test-pager\"" : "/bin/sh -c \"test-pager\"",
            Command(Pager.Find(new Dictionary<string, string> { ["PAGER"] = "test-pager", ["PATH"] = folder }, windows)));
        Assert.Equal($"{less} -R", Command(Pager.Find(new Dictionary<string, string> { ["PAGER"] = " ", ["PATH"] = folder }, windows)));
        Assert.Equal(windows ? "more.com" : null, Command(Pager.Find(new Dictionary<string, string> { ["PATH"] = sof.Directory }, windows)));
    }

    // $PAGER is given to the shell as one argument, as .NET splits a command line; to cmd as it is, between quotes /s strips.
    [Theory]
    [InlineData("less -R", "\"less -R\"")]
    [InlineData("less \"-P x\"", "\"less \\\"-P x\\\"\"")]
    [InlineData(@"C:\tools\", @"""C:\tools\\""")]
    [InlineData(@"a\\""b", @"""a\\\\\""b""")]
    public void PAGER_is_quoted_as_one_argument(string pager, string quoted) => Assert.Equal(quoted, Pager.Quote(pager));

    [Fact]
    public void PAGER_with_quotes_reaches_cmd_as_it_is() =>
        Assert.Equal(@"/d /s /c """"C:\Program Files\Git\usr\bin\less.exe"" -R""", Pager.CmdArguments(@"""C:\Program Files\Git\usr\bin\less.exe"" -R"));

    private static string Points(int count) => string.Join("\n", Enumerable.Range(1, count).Select(number => $"Point {number}."));

    /// <summary>A reply streamed a line at a time, as a model writes it.</summary>
    private static ModelEvent[] Streamed(string text) =>
        [.. text.Split('\n').Select((line, index) => new TextDelta(index == 0 ? line : "\n" + line)), new Stopped(StopReason.Finished)];

    /// <summary>Whether any text the agent was given holds <paramref name="text"/>: the lead's later work follows its plan in its conversation.</summary>
    private static bool Given(ModelRequest request, string text) =>
        request.History.Any(message => message.Content.OfType<TextContent>().Any(content => content.Text.Contains(text, StringComparison.Ordinal)));

    /// <summary>
    /// A model whose call number <paramref name="stalling"/> stalls once its text is written, before it stops, until
    /// <paramref name="release"/> completes or the call is cancelled; <paramref name="stalled"/> is set as it stalls.
    /// </summary>
    private sealed class StallingModel(ScriptedModelProvider inner, int stalling, Task release, TaskCompletionSource? stalled = null) : IModelProvider
    {
        private int calls;

        public ProviderCapabilities CapabilitiesOf(string model) => inner.CapabilitiesOf(model);

        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var stalls = Interlocked.Increment(ref calls) == stalling;
            await foreach (var modelEvent in inner.StreamAsync(request, ct))
            {
                if (modelEvent is Stopped && stalls)
                {
                    stalled?.TrySetResult();
                    await release.WaitAsync(ct);
                }

                yield return modelEvent;
            }
        }
    }
}
