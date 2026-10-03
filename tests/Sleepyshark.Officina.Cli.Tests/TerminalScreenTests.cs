namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// The chat session's terminal: what the session prints and what the line editor draws share the screen, and nothing printed is
/// lost. The terminal is a stand-in: the escape sequences are checked, not drawn.
/// </summary>
public sealed class TerminalScreenTests : IDisposable
{
    private const string ClearLine = "\r\u001b[2K";

    private readonly StringWriter terminal = new() { NewLine = "\n" };
    private readonly TerminalScreen screen;

    public TerminalScreenTests() => screen = new TerminalScreen(terminal);

    public void Dispose() => terminal.Dispose();

    // The editor drawing, as it does for a key typed while a streamed line is half written, ends that line first, so its redraw,
    // which clears the line it is on, lands below the text instead of erasing it; the rest of the stream then starts a line of its own.
    [Fact]
    public void The_editor_drawing_mid_line_ends_the_line_so_it_draws_below_the_streamed_text()
    {
        var output = screen.Writer(terminal);
        output.NewLine = "\n";

        output.Write("[dev] STREAMED partial line ");
        screen.Draw("\u001b[2K\u001b[1G> t");
        output.Write("one\n");
        output.Write("second line\n");

        Assert.Equal(
            $"{ClearLine}[dev] STREAMED partial line \n\u001b[2K\u001b[1G> t{ClearLine}one\n{ClearLine}second line\n",
            terminal.ToString());
    }

    // Printed text that starts a line clears the editor's prompt first; text that continues a line does not.
    [Fact]
    public void Printed_text_clears_the_prompt_only_where_it_starts_a_line()
    {
        var output = screen.Writer(terminal);
        output.NewLine = "\n";

        screen.Draw("> ");
        output.Write("[dev] Hel");
        output.Write("lo.");
        output.WriteLine();

        Assert.Equal($"> {ClearLine}[dev] Hello.\n", terminal.ToString());
    }

    // Standard error shares the screen: an error clears the prompt line too, and goes to its own stream.
    [Fact]
    public void An_error_clears_the_prompt_line_and_goes_to_standard_error()
    {
        using var standardError = new StringWriter();
        var errors = screen.Writer(standardError);
        errors.NewLine = "\n";

        screen.Draw("> ");
        errors.WriteLine("error: Required argument missing.");

        Assert.Equal($"> {ClearLine}", terminal.ToString());
        Assert.Equal("error: Required argument missing.\n", standardError.ToString());
    }
}
