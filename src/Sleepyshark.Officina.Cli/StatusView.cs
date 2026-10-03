using System.Globalization;
using System.Text;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Workspace;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// What each agent is doing, and the cost so far, from the events it is given (UX-01). Each change is printed as a line as it
/// happens; <see cref="Print"/> shows everything at a glance, with what waits for the owner. One view can follow several
/// runs one after another, as in <c>sof chat</c>, and its cost is then theirs together.
/// </summary>
/// <param name="output">Where the lines go.</param>
/// <param name="queue">The integration queue's length and waiting time, when the workspace is on (WS-09).</param>
/// <param name="stream">Whether the model's text is printed as it is generated (LAT-02), each agent's after its name.</param>
/// <param name="fold">
/// Whether long text is folded, as in a chat session at a terminal: the text of one model call that runs past
/// <see cref="LongLines"/> lines shows its first <see cref="ShownLines"/>, then a line that counts the rest as it comes, which
/// ends as <c>… N more lines</c>. Text of up to <see cref="LongLines"/> lines is printed in full: its lines after the first
/// <see cref="ShownLines"/> once the call ends, as until then it may still grow long.
/// </param>
internal sealed class StatusView(TextWriter output, Func<IntegrationQueueStatus?>? queue = null, bool stream = false, bool fold = false)
{
    /// <summary>Text of more lines than this is long, and folded.</summary>
    internal const int LongLines = 20;

    /// <summary>The lines long text shows before it is folded.</summary>
    internal const int ShownLines = 12;

    private const string ClearLine = "\r\u001b[2K";

    private readonly Dictionary<string, string> doing = new(StringComparer.Ordinal);
    private decimal cost;

    /// <summary>The agent whose text was printed last, until a line ends it.</summary>
    private string? streaming;

    /// <summary>Whether the last thing printed is text that has not ended its line.</summary>
    private bool midLine;

    /// <summary>The text each agent writes in its model call now, and how much of it is printed.</summary>
    private readonly Dictionary<string, Block> blocks = new(StringComparer.Ordinal);

    /// <summary>The line that counts folded text, while it is the last thing printed: it is rewritten in place.</summary>
    private string? progress;

    /// <summary>The text of the model call that ended last.</summary>
    private string? lastText;

    /// <summary>The cost of the events applied so far.</summary>
    public decimal Cost
    {
        get
        {
            lock (doing)
            {
                return cost;
            }
        }
    }

    /// <summary>A writer whose lines are printed as <see cref="WriteLine"/> prints them, for what else shares the view's output.</summary>
    public TextWriter Writer() => TextWriter.Synchronized(new LineWriter(this));

    public void Apply(CoreEvent coreEvent)
    {
        ArgumentNullException.ThrowIfNull(coreEvent);
        string? now = coreEvent.Payload switch
        {
            TurnStarted => "working",
            ToolCallStarted started => $"running {started.Tool}",
            ToolCallEnded { Error: { } error } ended => $"{ended.Tool} failed: {error}",
            BudgetWarning warning => $"the {warning.Level} {warning.Limit} budget is {warning.Used:P0} used",
            HumanAsked asked => $"waits for you: {asked.Request} {asked.Tool}".TrimEnd(),
            HumanAnswered { TimedOut: true } => "working; nobody answered in time",
            HumanAnswered => "working",
            TurnEnded ended => $"ended: {ended.Outcome}{(ended.Reason is { } reason ? $" ({reason})" : "")}",
            AgentStatusChanged changed => $"{changed.Status.ToString().ToLowerInvariant()}{(changed.Detail is { } detail ? $": {detail}" : "")}",
            MessageSent sent => $"sent {sent.To} a message",
            Warning warning => $"warning: {warning.Text}",
            ModelCallRetried retried when stream => $"the model call is tried again ({retried.Failure}), so its text above is void",
            _ => null,
        };
        lock (doing)
        {
            if (coreEvent.Payload is TextGenerated text && stream)
            {
                Text(coreEvent.Agent, text.Text);
            }

            if (coreEvent.Payload is ModelCallEnded or ModelCallRetried or TurnEnded)
            {
                EndText(coreEvent.Agent);
            }

            if (coreEvent.Payload is ModelCallEnded call)
            {
                cost += call.Cost;
                // Costs are written the same in every culture, so whatever reads the console reads them alike.
                Line(string.Create(CultureInfo.InvariantCulture, $"[{coreEvent.Agent}] model call: {call.Usage.Total} tokens, ${call.Cost:0.00}; cost so far ${cost:0.00}"));
            }

            if (now is not null)
            {
                doing[coreEvent.Agent] = now;
                Line($"[{coreEvent.Agent}] {now}");
            }
        }
    }

    /// <summary>
    /// Ends the text the agents write, as their run has ended, and returns the text of the model call that ended last, such as
    /// what the agent was writing when the run was cancelled; null if there is none.
    /// </summary>
    public string? EndRun()
    {
        lock (doing)
        {
            foreach (var agent in blocks.Keys.ToList())
            {
                EndText(agent);
            }

            (var text, lastText) = (lastText, null);
            return text;
        }
    }

    /// <summary>Every agent's status, what waits for the owner, and the cost so far.</summary>
    public void Print(OwnerQueue owner)
    {
        lock (doing)
        {
            foreach (var (agent, now) in doing.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                Line($"{agent}: {now}");
            }

            foreach (var (number, request) in owner.Waiting)
            {
                Line($"waiting for you: #{number} {owner.Describe(number, request)}");
            }

            if (queue?.Invoke() is { } integration)
            {
                Line($"integration queue: {integration.Length} waiting, longest wait {integration.LongestWait:hh\\:mm\\:ss}");
            }

            Line(string.Create(CultureInfo.InvariantCulture, $"cost so far: ${cost:0.00}"));
        }
    }

    /// <summary>Prints a line of its own, after any text still on the line.</summary>
    public void WriteLine(string line)
    {
        lock (doing)
        {
            Line(line);
        }
    }

    /// <summary>The lines of a text: those its line breaks end, and one more if text follows the last.</summary>
    internal static int LineCount(string text) => text.Count(character => character == '\n') + (text.Length > 0 && !text.EndsWith('\n') ? 1 : 0);

    /// <summary>Text of a model call: printed as it comes, or with <c>fold</c> held and counted once it is past the first lines.</summary>
    private void Text(string agent, string text)
    {
        if (!blocks.TryGetValue(agent, out var block))
        {
            blocks[agent] = block = new Block();
        }

        block.Text.Append(text);
        if (!fold)
        {
            Show(agent, text);
            block.Printed = block.Text.Length;
            return;
        }

        if (!block.Folded)
        {
            // Up to the end of the first lines; nothing after them until the text is known to be short or long.
            var all = block.Text.ToString();
            var cut = Cut(all);
            if (cut > block.Printed)
            {
                Show(agent, all[block.Printed..cut]);
                block.Printed = cut;
            }

            block.Folded = LineCount(all) > LongLines;
        }

        if (block.Printed < block.Text.Length)
        {
            Progress($"[{agent}] … writing ({More(Hidden(block))} so far)");
        }
    }

    /// <summary>The agent's model call has ended: folded text says how much of it is not shown, and short text is printed in full.</summary>
    private void EndText(string agent)
    {
        if (!blocks.Remove(agent, out var block))
        {
            return;
        }

        var all = block.Text.ToString();
        if (all.Trim().Length > 0)
        {
            lastText = all;
        }

        if (block.Folded)
        {
            Line($"[{agent}] … {More(Hidden(block))}");
        }
        else if (block.Printed < all.Length)
        {
            Show(agent, all[block.Printed..]);
        }
    }

    /// <summary>Prints text an agent wrote, after its name when the last text printed was not its own.</summary>
    private void Show(string agent, string text)
    {
        EraseProgress();
        if (streaming != agent)
        {
            EndLine();
            output.Write($"[{agent}] ");
            streaming = agent;
        }

        output.Write(text);
        midLine = !text.EndsWith('\n');
    }

    /// <summary>The line that counts folded text: it replaces itself while it is the last thing printed, and is otherwise printed anew.</summary>
    private void Progress(string line)
    {
        if (line == progress)
        {
            return;
        }

        if (progress is not null)
        {
            output.Write(ClearLine + line);
        }
        else
        {
            EndLine();
            output.Write(line);
        }

        (progress, midLine) = (line, true);
    }

    /// <summary>Clears the line that counts folded text, when it is the last thing printed, so what is printed next takes its place.</summary>
    private void EraseProgress()
    {
        if (progress is not null)
        {
            output.Write(ClearLine);
            (progress, midLine) = (null, false);
        }
    }

    /// <summary>How many more lines there are, such as <c>9 more lines</c>.</summary>
    internal static string More(int lines) => lines == 1 ? "1 more line" : $"{lines} more lines";

    /// <summary>The lines of the agent's text not printed.</summary>
    private static int Hidden(Block block) => LineCount(block.Text.ToString()) - LineCount(block.Text.ToString(0, block.Printed));

    /// <summary>Where the first <see cref="ShownLines"/> lines of the text end: after the line break that ends the last of them, or at its end.</summary>
    private static int Cut(string text)
    {
        var at = -1;
        for (var line = 0; line < ShownLines; line++)
        {
            at = text.IndexOf('\n', at + 1);
            if (at < 0)
            {
                return text.Length;
            }
        }

        return at + 1;
    }

    /// <summary>A line of its own, after any text still on the line.</summary>
    private void Line(string line)
    {
        EraseProgress();
        EndLine();
        streaming = null;
        output.WriteLine(line);
    }

    private void EndLine()
    {
        if (midLine)
        {
            output.WriteLine();
            midLine = false;
        }
    }

    private sealed class Block
    {
        public StringBuilder Text { get; } = new();

        /// <summary>How many characters of the text are printed.</summary>
        public int Printed { get; set; }

        public bool Folded { get; set; }
    }

    /// <summary>Prints each line written to it as a line of the view; text that has not ended its line waits for the rest.</summary>
    private sealed class LineWriter(StatusView view) : TextWriter
    {
        private readonly StringBuilder pending = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void Write(string? value)
        {
            pending.Append(value);
            var text = pending.ToString();
            if (text.LastIndexOf('\n') is var end and >= 0)
            {
                view.WriteLine(text[..end].TrimEnd('\r').ReplaceLineEndings("\n"));
                pending.Clear().Append(text[(end + 1)..]);
            }
        }

        public override void WriteLine(string? value) => Write(value + "\n");

        public override void WriteLine() => Write("\n");
    }
}
