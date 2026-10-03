using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Storage.Sqlite;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// Where <c>sof</c> keeps its storage (STO-01): the SQLite database and the artifacts' folder that <c>operations.storage</c>
/// names, <c>.sof/sof.db</c> and <c>.sof/artifacts</c> in the project directory unless configured otherwise. A relative path
/// must stay in <c>.sof/</c>, which agents cannot see (WS-05); an absolute path must lead into the project's <c>.sof/</c> or out of
/// the project, so the storage is never where agents can read or change it.
/// </summary>
internal static class LocalStorage
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The storage's paths that lead somewhere they must not, for <c>config validate</c> and every command that opens it (CFG-06).</summary>
    public static IEnumerable<ConfigurationError> Errors(OfficinaOptions options, string directory)
    {
        if (options.Operations.Storage is not { } storage)
        {
            yield break;
        }

        foreach (var (setting, value) in new[] { ("path", storage.Path), ("artifacts", storage.Artifacts) })
        {
            if (value is { Length: > 0 } && Problem(value, directory) is { } problem)
            {
                yield return new ConfigurationError(
                    ValidationPhase.Shape, $"operations.storage.{setting}", problem,
                    $"Keep it in {WorkspaceOptions.StateFolder}/, as in \"{WorkspaceOptions.StateFolder}/{(setting == "path" ? "sof.db" : "artifacts")}\", or give an absolute path outside the project.");
            }
        }
    }

    /// <summary>The database file and the artifacts' folder, as full paths.</summary>
    public static (string Database, string Artifacts) Locate(OfficinaOptions options, string directory)
    {
        var database = Path.GetFullPath(options.Operations.Storage.Path, Path.GetFullPath(directory));
        return (database, options.Operations.Storage.Artifacts is { Length: > 0 } artifacts
            ? Path.GetFullPath(artifacts, Path.GetFullPath(directory))
            : SqliteStorage.ArtifactsBeside(database));
    }

    /// <summary>Opens the storage, making the database's folder if there is none.</summary>
    public static async Task<SqliteStorage> OpenAsync(OfficinaOptions options, string directory, CancellationToken ct)
    {
        var (database, artifacts) = Locate(options, directory);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        return await SqliteStorage.OpenAsync(database, artifacts, ct);
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
