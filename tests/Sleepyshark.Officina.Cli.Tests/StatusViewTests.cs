using System.Globalization;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>What <c>sof run</c> and <c>sof chat</c> show of a run's events (UX-01), and how a chat at a terminal folds long text.</summary>
public class StatusViewTests
{
    private long sequence;

    // The console's costs read the same in every culture, so a program reading them, such as the benchmark's runner, reads them right.
    [Fact]
    public void Costs_are_written_the_same_in_every_culture()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            using var output = new StringWriter();
            new StatusView(output).Apply(new CoreEvent("run", "dev", null, 1, DateTimeOffset.UnixEpoch, new ModelCallEnded(StopReason.Finished, new Usage(10, 0, 0, 0), 12.34m)));

            Assert.Equal("[dev] model call: 10 tokens, $12.34; cost so far $12.34\n", output.ToString().ReplaceLineEndings("\n"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // Long text shows its first 12 lines as they come, then one line that says it is folded, and
    // nothing more until the call ends, which says how much is not shown.
    [Fact]
    public void Long_text_shows_its_first_lines_then_one_line_until_the_call_ends()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true, fold: true);

        Stream(view, "dev", 30);
        view.Apply(Event("dev", new ModelCallEnded(StopReason.Finished, new Usage(10, 0, 0, 0), 0m)));

        Assert.Equal(
            $"[dev] {Lines(1, 12)}[dev] … (folded; /show to read)\n[dev] … 18 more lines\n[dev] model call: 10 tokens, $0.00; cost so far $0.00\n",
            output.ToString());
        Assert.Equal((Lines(1, 30).TrimEnd(), 18), view.EndRun());
    }

    // Text of 20 lines is not long: what was held after the first 12 is printed in full when the call ends.
    [Fact]
    public void Text_up_to_the_limit_is_printed_in_full()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true, fold: true);

        Stream(view, "dev", 20);
        Assert.Equal($"[dev] {Lines(1, 12)}", output.ToString());
        view.Apply(Event("dev", new ModelCallEnded(StopReason.Finished, new Usage(10, 0, 0, 0), 0m)));

        Assert.StartsWith($"[dev] {Lines(1, 20)}[dev] model call", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, view.EndRun().Hidden);
    }

    // Before the owner is asked something, what the asking agent's last call folded is printed in full, and is no longer
    // counted as not shown.
    [Fact]
    public void Folded_text_is_printed_before_its_agent_asks_the_owner()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true, fold: true);
        var requests = view.Writer();
        requests.NewLine = "\n";

        Stream(view, "dev", 29);
        view.Apply(Event("dev", new ModelCallEnded(StopReason.WantsTools, new Usage(10, 0, 0, 0), 0m)));
        view.Unfold("lead"); // another agent folded nothing
        view.Unfold("dev");
        requests.WriteLine("#1 dev asks to run write_file {}: write.");
        view.Unfold("dev"); // once

        Assert.EndsWith(
            $"[dev] … 17 more lines\n[dev] model call: 10 tokens, $0.00; cost so far $0.00\n[dev] … the folded lines, as you are asked:\n[dev] {Lines(13, 29)}#1 dev asks to run write_file {{}}: write.\n",
            output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, view.EndRun().Hidden);
    }

    // What the agent folded in every call since it last asked is printed before it asks, each call's on lines of its own.
    [Fact]
    public void Folded_text_of_every_call_since_the_agent_last_asked_is_printed_before_it_asks()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true, fold: true);

        view.Apply(Event("dev", new TextGenerated(string.Join("\n", Enumerable.Range(1, 22).Select(number => $"A{number}")))));
        view.Apply(Event("dev", new ModelCallEnded(StopReason.WantsTools, new Usage(1, 0, 0, 0), 0m)));
        view.Apply(Event("dev", new TextGenerated("Short.")));
        view.Apply(Event("dev", new ModelCallEnded(StopReason.WantsTools, new Usage(1, 0, 0, 0), 0m)));
        Stream(view, "dev", 21);
        view.Apply(Event("dev", new ModelCallEnded(StopReason.WantsTools, new Usage(1, 0, 0, 0), 0m)));
        view.Unfold("dev");

        var unfolded = string.Join("\n", Enumerable.Range(13, 10).Select(number => $"A{number}")) + "\n" + Lines(13, 21);
        Assert.EndsWith($"[dev] … the folded lines, as you are asked:\n[dev] {unfolded}", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, view.EndRun().Hidden);
    }

    // Two agents' text interleaved: each keeps its name where its text starts a line again, and each folds on its own. A run's
    // text has each model call's text, after its agent's name.
    [Fact]
    public void Two_agents_text_folds_each_on_its_own_and_keeps_their_names()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true, fold: true);

        view.Apply(Event("lead", new TextGenerated("Hello wor")));
        Stream(view, "dev", 25);
        view.Apply(Event("lead", new TextGenerated("ld, and more.")));
        view.Apply(Event("dev", new ModelCallEnded(StopReason.Finished, new Usage(1, 0, 0, 0), 0m)));
        view.Apply(Event("lead", new ModelCallEnded(StopReason.Finished, new Usage(1, 0, 0, 0), 0m)));

        Assert.StartsWith($"[lead] Hello wor\n[dev] {Lines(1, 12)}[dev] … (folded; /show to read)\n[lead] ld, and more.\n[dev] … 13 more lines\n", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(($"[dev] {Lines(1, 25).TrimEnd()}\n\n[lead] Hello world, and more.", 13), view.EndRun());
    }

    // sof run and piped chats print every line as it comes.
    [Fact]
    public void Without_folding_every_line_is_printed()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true);

        Stream(view, "dev", 30);
        view.Apply(Event("dev", new ModelCallEnded(StopReason.Finished, new Usage(10, 0, 0, 0), 0m)));

        Assert.StartsWith($"[dev] {Lines(1, 30)}[dev] model call", output.ToString(), StringComparison.Ordinal);
        Assert.Equal((Lines(1, 30).TrimEnd(), 0), view.EndRun());
    }

    // A run's text is every model call's, so far while it runs, and what was being written when it was cancelled at its end.
    [Fact]
    public void The_run_s_text_is_every_call_s()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true, fold: true);

        view.Apply(Event("dev", new TextGenerated("Let me look.")));
        view.Apply(Event("dev", new ModelCallEnded(StopReason.WantsTools, new Usage(1, 0, 0, 0), 0m)));
        Stream(view, "dev", 25);
        Assert.Equal($"Let me look.\n\n{Lines(1, 25).TrimEnd()}", view.SoFar());
        var (text, hidden) = view.EndRun();

        Assert.Equal($"Let me look.\n\n{Lines(1, 25).TrimEnd()}", text);
        Assert.Equal(13, hidden);
        Assert.EndsWith("[dev] … 13 more lines\n", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(("", 0), view.EndRun());
    }

    // A failed tool call says why, on one line and cut short, rather than only its category.
    [Fact]
    public void A_failed_tool_call_shows_its_reason_on_one_line()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output);

        view.Apply(Event("developer[1]", new ToolCallEnded("write_file", ToolErrorCategory.Failed, "failed (IOException: Access to the path 'src/App.cs' is denied.)")));
        view.Apply(Event("developer[1]", new ToolCallEnded("run_command", ToolErrorCategory.PolicyViolation, $"policy violation: {new string('x', 300)}")));
        view.Apply(Event("developer[1]", new ToolCallEnded("read_file", ToolErrorCategory.Failed)));

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("[developer[1]] write_file: failed (IOException: Access to the path 'src/App.cs' is denied.)", lines[0]);
        Assert.StartsWith("[developer[1]] run_command: policy violation: xxx", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("x…", lines[1], StringComparison.Ordinal);
        Assert.Equal("[developer[1]] read_file: Failed", lines[2]); // an event with no reason, from before reasons were kept
        Assert.Equal(3, lines.Length);
    }

    private static string Lines(int from, int to) => string.Concat(Enumerable.Range(from, to - from + 1).Select(number => $"Line {number}\n"));

    private void Stream(StatusView view, string agent, int lines)
    {
        for (var number = 1; number <= lines; number++)
        {
            view.Apply(Event(agent, new TextGenerated($"Line {number}\n")));
        }
    }

    private CoreEvent Event(string agent, EventPayload payload) => new("run", agent, null, ++sequence, DateTimeOffset.UnixEpoch, payload);
}
