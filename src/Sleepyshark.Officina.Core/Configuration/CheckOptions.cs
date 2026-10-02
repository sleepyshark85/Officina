using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// A check of output or of work (OUT-03, TASK-05, WS-02): the application's, or a command. Only its result decides whether the
/// work is accepted (INV-09).
/// </summary>
public sealed record CheckOptions
{
    [Setting("The check the application registers, as `extension:<id>`. Set it or `command`.", Example = "\"extension:Acme.NoSecretsCheck\"")]
    public string? Use { get; init; }

    [Setting("A command that checks a working copy, run in the sandbox there: the check passes when it exits with 0, and its last lines of output are the findings. It needs the sandbox, and checks a task's work or the baseline, not an agent's output. Set it or `use`.",
        Example = "\"dotnet test\"")]
    public string? Command { get; init; }

    [Setting("For a command: the longest it may run, as `hh:mm:ss`; then it is stopped, and the check fails.", Example = "\"00:40:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = "must be greater than zero.")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>The id of the application's check.</summary>
    public string? ExtensionId() => Use is null ? null : ToolOptions.After(Use, "extension:");

    /// <summary>The id the check's implementation is registered under: the application's, or <c>command:&lt;name&gt;</c> for a command, which the host registers.</summary>
    /// <param name="name">The check's name in <c>checks</c>.</param>
    public string Id(string name) => ExtensionId() ?? CommandId(name);

    /// <summary>The id a host registers a command check under.</summary>
    public static string CommandId(string name) => $"command:{name}";
}
