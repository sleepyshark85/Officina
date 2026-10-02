using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Providers.Claude.Tests;

/// <summary>
/// The recorded exchanges the tests replay, kept in the <c>Recordings</c> folder beside the tests. The Claude API is the
/// boundary (DESIGN.md §11): the provider and the SDK run for real, against responses replayed over HTTP.
/// </summary>
internal static class Recordings
{
    private static readonly string Folder = Path.Combine(FindRoot(), "tests", "Sleepyshark.Officina.Providers.Claude.Tests", "Recordings");

    public static string Named(string name) => Path.Combine(Folder, name);

    /// <summary>A provider whose requests are answered from the recording, with a key that is never checked.</summary>
    public static ClaudeProvider Replay(string path, ProviderOptions? options = null) =>
        new(options ?? ProviderOptions.Claude, new InMemorySecretSource(new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "test-key" }), HttpRecording.Replay(path));

    public static async Task<List<ModelEvent>> StreamAsync(ClaudeProvider provider, ModelRequest request)
    {
        var events = new List<ModelEvent>();
        await foreach (var modelEvent in provider.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(modelEvent);
        }

        return events;
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sleepyshark.Officina.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}
