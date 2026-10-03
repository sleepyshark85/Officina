using System.Globalization;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>What <c>sof run</c> and <c>sof chat</c> show of a run's events (UX-01), and how a chat at a terminal folds long text.</summary>
public class StatusViewTests
{
    private const string ClearLine = "\r\u001b[2K";

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

    // Text of 21 lines, one more than is long, shows its first 12 as they come; then one line counts the rest in place, and ends
    // as what was not shown.
    [Fact]
    public void Long_text_shows_its_first_lines_then_counts_the_rest_in_place()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true, fold: true);

        Stream(view, "dev", 21);
        view.Apply(Event("dev", new ModelCallEnded(StopReason.Finished, new Usage(10, 0, 0, 0), 0m)));

        var expected = "[dev] " + Lines(1, 12) + "[dev] … writing (1 more line so far)"
            + string.Concat(Enumerable.Range(2, 8).Select(more => $"{ClearLine}[dev] … writing ({more} more lines so far)"))
            + $"{ClearLine}[dev] … 9 more lines\n[dev] model call: 10 tokens, $0.00; cost so far $0.00\n";
        Assert.Equal(expected, output.ToString());
    }

    // Text of 20 lines is not long: what was held after the first 12 is printed in full when the call ends, in place of the count.
    [Fact]
    public void Text_up_to_the_limit_is_printed_in_full()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true, fold: true);

        Stream(view, "dev", 20);
        view.Apply(Event("dev", new ModelCallEnded(StopReason.Finished, new Usage(10, 0, 0, 0), 0m)));

        var text = output.ToString();
        Assert.Contains($"{ClearLine}{Lines(13, 20)}[dev] model call", text, StringComparison.Ordinal);
        Assert.DoesNotContain("more lines\n", text, StringComparison.Ordinal);
        Assert.Equal(20, text.Split('\n').Count(line => line.Contains("Line ", StringComparison.Ordinal)));
    }

    // A line printed while text is folded, such as a request that waits for the owner, takes the count's place, never runs into
    // it, and is never folded; the count comes back below it.
    [Fact]
    public void A_line_printed_while_text_is_folded_takes_the_count_s_place()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true, fold: true);
        var requests = view.Writer();
        requests.NewLine = "\n";

        Stream(view, "dev", 14);
        requests.WriteLine("#1 lead needs your sign-off: Approve the plan?\n  a Parse\n  b Print\nAnswer with /approve 1 or /deny 1.");
        view.Apply(Event("dev", new TextGenerated("Line 15\n")));

        Assert.EndsWith(
            $"[dev] … writing (2 more lines so far){ClearLine}#1 lead needs your sign-off: Approve the plan?\n  a Parse\n  b Print\nAnswer with /approve 1 or /deny 1.\n[dev] … writing (3 more lines so far)",
            output.ToString(), StringComparison.Ordinal);
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
        Assert.Equal(Lines(1, 30), view.EndRun());
    }

    // The text of the call that ended last is what the run ends with, such as what was being written when the reply was cancelled.
    [Fact]
    public void The_run_ends_with_the_text_written_last()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var view = new StatusView(output, stream: true, fold: true);

        Stream(view, "dev", 25);
        var text = view.EndRun();

        Assert.Equal(Lines(1, 25), text);
        Assert.EndsWith($"{ClearLine}[dev] … 13 more lines\n", output.ToString(), StringComparison.Ordinal);
        Assert.Null(view.EndRun());
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
