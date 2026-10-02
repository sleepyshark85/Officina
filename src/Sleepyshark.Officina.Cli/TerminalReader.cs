using RadLine;
using Spectre.Console;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The console at a terminal: each line is read with RadLine's line editor, so Tab completes what <see cref="Complete"/>
/// suggests, Up and Down go through the lines typed before, and Ctrl+D on an empty line ends the input. The editor waits for
/// keys without blocking its thread, and a read can be cancelled. Where the input is not a terminal, sof reads plain lines
/// instead (<see cref="For"/>).
/// </summary>
internal sealed class TerminalReader : TextReader, ITextCompletion
{
    private readonly LineEditor editor;

    private TerminalReader()
    {
        editor = new LineEditor(AnsiConsole.Console) { Prompt = new LineEditorPrompt("[grey]>[/]"), Completion = this };
        editor.KeyBindings.Add<PreviousHistoryCommand>(ConsoleKey.UpArrow);
        editor.KeyBindings.Add<NextHistoryCommand>(ConsoleKey.DownArrow);
        editor.KeyBindings.Add(ConsoleKey.D, ConsoleModifiers.Control, () => new EndCommand(this));
    }

    /// <summary>What Tab suggests for a word, after what the line holds before it; nothing until the chat session sets it.</summary>
    public Func<string, string, IReadOnlyList<string>>? Complete { get; set; }

    /// <summary>The console of this process: the line editor at an interactive terminal, plain lines otherwise.</summary>
    public static TextReader For(TextReader plain) =>
        !Console.IsInputRedirected && LineEditor.IsSupported(AnsiConsole.Console) ? new TerminalReader() : plain;

    /// <summary>
    /// Standard output for a console read by <paramref name="input"/>. With the line editor, a read is always waiting, so its prompt
    /// is on the last line: each line of output clears it first, and the editor draws it again at the next key.
    /// </summary>
    public static TextWriter Output(TextWriter console, TextReader input) => input is TerminalReader ? new ClearingWriter(console) : console;

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await editor.ReadLine(cancellationToken).ConfigureAwait(false);
            if (line is not null || cancellationToken.IsCancellationRequested || ended)
            {
                ended = false;
                return line;
            }
        }
    }

    public override string? ReadLine() => ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    IEnumerable<string>? ITextCompletion.GetCompletions(string prefix, string word, string suffix) => Complete?.Invoke(prefix, word);

    /// <summary>Set by Ctrl+D on an empty line: the read returns null, the end of the input.</summary>
    private bool ended;

    /// <summary>Clears the line, which holds the editor's prompt, before each line it writes.</summary>
    private sealed class ClearingWriter(TextWriter console) : TextWriter
    {
        private bool lineStart = true;

        public override System.Text.Encoding Encoding => console.Encoding;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (console)
            {
                if (lineStart)
                {
                    console.Write("\r\u001b[2K");
                }

                console.Write(value);
                lineStart = value.EndsWith('\n');
            }
        }

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void WriteLine(string? value) => Write(value + NewLine);

        public override void Flush() => console.Flush();
    }

    private sealed class EndCommand(TerminalReader reader) : LineEditorCommand
    {
        public override void Execute(LineEditorContext context)
        {
            if (context.Buffer.Content.Length == 0)
            {
                reader.ended = true;
                context.Submit(SubmitAction.Cancel);
            }
        }
    }
}
