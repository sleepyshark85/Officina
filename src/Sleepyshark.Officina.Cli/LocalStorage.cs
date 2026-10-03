using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Storage.Sqlite;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// Where <c>sof</c> keeps its storage (STO-01): the SQLite database that <c>operations.storage.path</c> names, <c>.sof/sof.db</c> in
/// the project directory unless configured otherwise, with the artifacts' files in the folder <c>artifacts</c> beside it. A relative
/// path must stay in <c>.sof/</c>, which agents cannot see (WS-05); an absolute path must lead into the project's <c>.sof/</c> or out
/// of the project, so the storage is never where agents can read or change it.
/// </summary>
internal static class LocalStorage
{
    private const string Setting = "operations.storage.path";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>A database path that leads somewhere it must not, for <c>config validate</c> and every command that opens the storage (CFG-06).</summary>
    public static IEnumerable<ConfigurationError> Errors(OfficinaOptions options, string directory) =>
        options.Operations.Storage?.Path is { Length: > 0 } path && Problem(path, directory) is { } problem
            ? [new ConfigurationError(
                ValidationPhase.Shape, Setting, problem,
                $"Keep it in {WorkspaceOptions.StateFolder}/, as in \"{WorkspaceOptions.StateFolder}/sof.db\", or give an absolute path outside the project.")]
            : [];

    /// <summary>The database file, as a full path.</summary>
    public static string Database(OfficinaOptions options, string directory) =>
        Path.GetFullPath(options.Operations.Storage.Path, Path.GetFullPath(directory));

    /// <summary>Opens the storage, making the database's folder if there is none.</summary>
    public static async Task<SqliteStorage> OpenAsync(OfficinaOptions options, string directory, CancellationToken ct)
    {
        var database = Database(options, directory);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        return await SqliteStorage.OpenAsync(database, ct);
    }

    private static string? Problem(string value, string directory)
    {
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string full;
        try
        {
            full = Path.GetFullPath(value, project);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"\"{value}\" is not a valid path: {exception.Message}";
        }

        if (Inside(full, Path.Combine(project, WorkspaceOptions.StateFolder)))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(value))
        {
            return $"\"{value}\" leads out of {WorkspaceOptions.StateFolder}/: a relative path must stay in it, where agents cannot see the storage.";
        }

        return Inside(full, project) || string.Equals(full, project, PathComparison)
            ? $"\"{value}\" is in the project but not in {WorkspaceOptions.StateFolder}/, so agents could see the storage."
            : null;
    }

    private static bool Inside(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, PathComparison);
}
