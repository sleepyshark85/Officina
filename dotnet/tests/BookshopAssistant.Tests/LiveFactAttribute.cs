using System.Runtime.CompilerServices;

namespace BookshopAssistant.Tests;

/// <summary>
/// A live test: skipped unless <c>OFFICINA_LIVE_TESTS=1</c> and <c>ANTHROPIC_API_KEY</c> are set where the database tests
/// run (<see cref="BookshopDatabase.Available"/>), so neither <c>dotnet test</c> nor CI spends on it.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Live: set OFFICINA_LIVE_TESTS=1 and ANTHROPIC_API_KEY, on Linux with Docker.";
        SkipUnless = nameof(Enabled);
        SkipType = typeof(LiveFactAttribute);
    }

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("OFFICINA_LIVE_TESTS") == "1"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
        && BookshopDatabase.Available;
}
