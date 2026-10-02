using System.Text;
using RadLine;
using Spectre.Console;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The interactive terminal a chat session runs at: what the session prints and what the line editor draws share it under one
/// lock, so neither cuts into the other. The editor's prompt sits on the last line while a read waits. Printed text clears that
/// line before it starts a line, and when a key arrives while printed text has left a line unfinished, the line is ended first,
/// so the editor redraws its prompt below the text rather than over it. Nothing printed is lost.
/// </summary>
/// <param name="console">Where both write: the terminal's standard output.</param>
internal sealed class TerminalScreen(TextWriter console)
{
    private readonly Lock gate = new();

    /// <summary>Whether printed text has left the current line unfinished.</summary>
    private bool midLine;

    /// <summary>The terminal of this process, when its input and output are an interactive terminal the line editor supports.</summary>
    public static TerminalScreen? ForConsole() =>
        !Console.IsInputRedirected && LineEditor.IsSupported(AnsiConsole.Console) ? new TerminalScreen(Console.Out) : null;

    /// <summary>A writer for what the session prints to <paramref name="stream"/>, standard output or error, on this terminal.</summary>
    public TextWriter Writer(TextWriter stream) => new PrintWriter(this, stream);

    /// <summary>The console read through the line editor, with Tab completion and history.</summary>
    public TerminalReader Reader() => new(EditorConsole(), new Keys(this));

    /// <summary>Prints text: a line it starts first clears what is there, which is the editor's prompt or nothing.</summary>
    internal void Print(TextWriter stream, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        lock (gate)
        {
            if (!midLine)
            {
                console.Write("\r\u001b[2K");
                console.Flush();
            }

            stream.Write(text);
            stream.Flush();
            midLine = !text.EndsWith('\n');
        }
    }

    /// <summary>The editor draws: its prompt and what was typed, on a line of its own.</summary>
    internal void Draw(string text)
    {
        lock (gate)
        {
            console.Write(text);
            console.Flush();
        }
    }

    /// <summary>A key arrives: a line printed text left unfinished is ended, so the editor draws below it.</summary>
    internal void KeyArrives()
    {
        lock (gate)
        {
            if (midLine)
            {
                console.WriteLine();
                console.Flush();
                midLine = false;
            }
        }
    }

    private IAnsiConsole EditorConsole() => AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new EditorOutput(this),
        Ansi = AnsiSupport.Yes,
        Interactive = InteractionSupport.Yes,
        ColorSystem = ColorSystemSupport.Detect,
    });

    private sealed class PrintWriter(TerminalScreen screen, TextWriter stream) : TextWriter
    {
        public override Encoding Encoding => stream.Encoding;

        public override void Write(char value) => screen.Print(stream, value.ToString());

        public override void Write(string? value) => screen.Print(stream, value ?? "");

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void WriteLine(string? value) => Write(value + NewLine);

        public override void Flush() => stream.Flush();
    }

    /// <summary>The editor's output: the terminal, under the screen's lock.</summary>
    private sealed class EditorOutput(TerminalScreen screen) : IAnsiConsoleOutput
    {
        public TextWriter Writer { get; } = new DrawWriter(screen);

        public bool IsTerminal => true;

        public int Width => AnsiConsole.Profile.Width; // as the default console measures the terminal

        public int Height => AnsiConsole.Profile.Height;

        public void SetEncoding(Encoding encoding)
        {
        }
    }

    private sealed class DrawWriter(TerminalScreen screen) : TextWriter
    {
        public override Encoding Encoding => Console.OutputEncoding;

        public override void Write(char value) => screen.Draw(value.ToString());

        public override void Write(string? value) => screen.Draw(value ?? "");

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    }

    /// <summary>The keyboard, as the editor reads it: it waits for a key without blocking, and each key ends an unfinished printed line.</summary>
    private sealed class Keys(TerminalScreen screen) : IInputSource
    {
        public bool ByPassProcessing => false;

        public bool IsKeyAvailable() => Console.KeyAvailable;

        public ConsoleKeyInfo ReadKey()
        {
            var key = Console.ReadKey(intercept: true);
            screen.KeyArrives();
            return key;
        }
    }
}

/// <summary>
/// The console of a chat session at a terminal: each line is read with RadLine's line editor, so Tab completes what
/// <see cref="Complete"/> suggests, Up and Down go through the lines typed before, and Ctrl+D on an empty line ends the input.
/// The editor waits for keys without blocking its thread, and a read can be cancelled.
/// </summary>
internal sealed class TerminalReader : TextReader, ITextCompletion
{
    private readonly LineEditor editor;

    /// <summary>Set by Ctrl+D on an empty line: the read returns null, the end of the input.</summary>
    private bool ended;

    public TerminalReader(IAnsiConsole terminal, IInputSource keys)
    {
        editor = new LineEditor(terminal, keys) { Prompt = new LineEditorPrompt("[grey]>[/]"), Completion = this };
        editor.KeyBindings.Add<PreviousHistoryCommand>(ConsoleKey.UpArrow);
        editor.KeyBindings.Add<NextHistoryCommand>(ConsoleKey.DownArrow);
        editor.KeyBindings.Add(ConsoleKey.D, ConsoleModifiers.Control, () => new EndCommand(this));
    }

    /// <summary>What Tab suggests for a word, after what the line holds before it; nothing until the chat session sets it.</summary>
    public Func<string, string, IReadOnlyList<string>>? Complete { get; set; }

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

    IEnumerable<string>? ITextCompletion.GetCompletions(string prefix, string word, string suffix)
    {
        try
        {
            return Complete?.Invoke(prefix, word);
        }
        catch (Exception)
        {
            // A completion that fails offers nothing; it never ends the read, nor the session.
            return null;
        }
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
