using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// <c>/show</c>'s pager: <c>$PAGER</c>, run by the shell as git runs it; else <c>less -R</c> when it is on the path; else
/// <c>more</c> on Windows. It gets the text on its standard input and the terminal for the rest.
/// </summary>
internal static class Pager
{
    /// <summary>The program that pages, and its arguments; null when there is none, so the text is printed instead.</summary>
    public static (string Program, string[] Arguments)? Find(IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (Variable(variables, "PAGER") is { } pager && pager.Trim().Length > 0)
        {
            return OperatingSystem.IsWindows() ? ("cmd.exe", ["/c", pager]) : ("/bin/sh", ["-c", pager]);
        }

        if (OnPath("less", variables) is { } less)
        {
            return (less, ["-R"]);
        }

        return OperatingSystem.IsWindows() ? ("more.com", []) : null;
    }

    /// <summary>
    /// Runs the program in the foreground, with <paramref name="text"/> as its standard input and the terminal as its output, and
    /// returns once it ends; false if it could not be started. While it runs, .NET gives the terminal the settings it had when
    /// the process started, and restores its own when the program ends.
    /// </summary>
    public static async Task<bool> RunAsync(string program, IReadOnlyList<string> arguments, string text)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardInput = true, StandardInputEncoding = new UTF8Encoding(false) };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            return false;
        }

        if (process is null)
        {
            return false;
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
            });
            await process.WaitForExitAsync();
            await writing;
        }

        return true;
    }

    /// <summary>The program's full path when it is in a folder of <c>PATH</c>; null otherwise.</summary>
    private static string? OnPath(string program, IReadOnlyDictionary<string, string> variables) =>
        (Variable(variables, "PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(folder => OperatingSystem.IsWindows() ? [Path.Combine(folder, $"{program}.exe")] : new[] { Path.Combine(folder, program) })
            .FirstOrDefault(File.Exists);

    /// <summary>An environment variable; on Windows its name is in any case, as <c>Path</c> is.</summary>
    private static string? Variable(IReadOnlyDictionary<string, string> variables, string name) =>
        variables.FirstOrDefault(variable => string.Equals(variable.Key, name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value;
}
