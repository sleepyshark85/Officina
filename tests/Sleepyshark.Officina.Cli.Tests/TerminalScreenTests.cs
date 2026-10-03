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

    // The editor pads the line it draws with spaces to the terminal's width; it clears the line first and places the cursor by
    // its column, so the padding is left out, and a copy of the screen has no runs of spaces after what was typed.
    [Fact]
    public void The_editor_s_padding_to_the_terminal_s_width_is_left_out()
    {
        screen.Draw("\u001b[2K\u001b[1G> approve 1                    \u001b[12G");
        screen.Draw("\u001b[2K\u001b[1G> approve 1                    \u001b[3G");

        Assert.Equal("\u001b[2K\u001b[1G> approve 1\u001b[12G\u001b[2K\u001b[1G> approve 1\u001b[3G", terminal.ToString());
    }

    // While the terminal is handed over to a pager, nothing is printed or drawn over it; what came meanwhile follows, in order,
    // once it is taken back.
    [Fact]
    public void What_comes_while_the_terminal_is_handed_over_waits_and_then_follows_in_order()
    {
        var output = screen.Writer(terminal);
        output.NewLine = "\n";

        screen.Draw("> ");
        var handle = screen.HandOver();
        output.WriteLine("[dev] model call: 10 tokens");
        screen.Draw("\u001b[2K\u001b[1G> ");
        output.WriteLine("#1 dev asks to run note");
        Assert.Equal($"> {ClearLine}", terminal.ToString()); // the prompt is cleared for the program

        handle.Dispose();
        handle.Dispose(); // taken back once

        Assert.Equal($"> {ClearLine}{ClearLine}[dev] model call: 10 tokens\n\u001b[2K\u001b[1G> {ClearLine}#1 dev asks to run note\n", terminal.ToString());
    }

    // A program the terminal is handed to, such as a pager that writes in place, starts on a line of its own, and what it
    // wrote stays: printing afterwards starts where it left the cursor, at the start of a line.
    [Fact]
    public void The_program_starts_on_a_line_of_its_own_and_what_it_wrote_stays()
    {
        var output = screen.Writer(terminal);
        output.NewLine = "\n";

        output.Write("[dev] partial");
        using (screen.HandOver())
        {
            terminal.Write("Done.\n"); // the pager, writing to the terminal itself
        }

        output.WriteLine("next");

        Assert.Equal($"{ClearLine}[dev] partial\nDone.\n{ClearLine}next\n", terminal.ToString());
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
