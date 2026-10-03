using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// <c>/show</c>'s pager: <c>$PAGER</c>, run by the shell as git runs it; else <c>less -R</c> when it is on the path; else
/// <c>more</c> on Windows. It gets the text on its standard input and the terminal for the rest.
/// </summary>
internal static class Pager
{
    private const int Terminate = 15; // SIGTERM

    /// <summary>
    /// The program that pages, and its command line; null when there is none, so the text is printed instead. The command line
    /// is one string, as the shell gets <c>$PAGER</c> as it is.
    /// </summary>
    public static (string Program, string Arguments)? Find(IReadOnlyDictionary<string, string> variables) => Find(variables, OperatingSystem.IsWindows());

    internal static (string Program, string Arguments)? Find(IReadOnlyDictionary<string, string> variables, bool windows)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (Variable(variables, "PAGER", windows) is { } pager && pager.Trim().Length > 0)
        {
            return windows ? ("cmd.exe", CmdArguments(pager)) : ("/bin/sh", $"-c {Quote(pager)}");
        }

        if (OnPath("less", variables, windows) is { } less)
        {
            return (less, "-R");
        }

        return windows ? ("more.com", "") : null;
    }

    /// <summary>
    /// cmd's command line for a command, such as <c>"C:\Program Files\Git\usr\bin\less.exe" -R</c>: with <c>/s</c>, cmd takes
    /// everything between the first and the last quote as it is, so the command keeps its own quotes.
    /// </summary>
    internal static string CmdArguments(string command) => $"/d /s /c \"{command}\"";

    /// <summary>
    /// One argument quoted for a command line, as .NET splits one on every system and Windows programs do: in quotes, with a
    /// quote and the backslashes before it, or before the closing quote, escaped.
    /// </summary>
    internal static string Quote(string argument)
    {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', character == '"' ? (backslashes * 2) + 1 : backslashes).Append(character);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// Runs the program in the foreground, with <paramref name="text"/> as its standard input and the terminal as its output, and
    /// returns its exit code once it ends; null if it could not be started. While it runs, .NET gives the terminal the settings
    /// it had when the process started, and restores its own when the program ends. Cancelled, as when the session ends, the
    /// program is asked to end, so a pager restores the screen, and is killed if it has not ended a second later.
    /// </summary>
    public static async Task<int?> RunAsync(string program, string arguments, string text, CancellationToken ct)
    {
        var start = new ProcessStartInfo(program, arguments) { UseShellExecute = false, RedirectStandardInput = true, StandardInputEncoding = new UTF8Encoding(false) };
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            var writing = Task.Run(async () =>
            {
                try
                {
                    await process.StandardInput.WriteAsync(text);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The pager was quit before it read everything.
                }
            }, CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Stop(process);
                throw;
            }
            finally
            {
                await writing;
            }

            return process.ExitCode;
        }
    }

    /// <summary>Asks the program to end (SIGTERM), then kills it, and what it started, if it has not ended within a second.</summary>
    private static void Stop(Process process)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                _ = Kill(process.Id, Terminate);
                if (process.WaitForExit(TimeSpan.FromSeconds(1)))
                {
                    return;
                }
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // It has ended meanwhile, or could not be stopped: either way the cancellation stands, and the session ends.
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    /// <summary>The program's full path when it is in a folder of <c>PATH</c>; null otherwise.</summary>
    private static string? OnPath(string program, IReadOnlyDictionary<string, string> variables, bool windows) =>
        (Variable(variables, "PATH", windows) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, windows ? $"{program}.exe" : program))
            .FirstOrDefault(File.Exists);

    /// <summary>An environment variable; on Windows its name is in any case, as <c>Path</c> is.</summary>
    private static string? Variable(IReadOnlyDictionary<string, string> variables, string name, bool windows) =>
        variables.FirstOrDefault(variable => string.Equals(variable.Key, name, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value;
}
