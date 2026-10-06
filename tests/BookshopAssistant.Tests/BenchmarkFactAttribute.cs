using System.Runtime.CompilerServices;

namespace BookshopAssistant.Tests;

/// <summary>
/// A benchmark: skipped unless <c>OFFICINA_BENCHMARK=1</c> is set where the database tests run
/// (<see cref="BookshopDatabase.Available"/>), as it loads a large catalogue and takes minutes.
/// </summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Benchmark: set OFFICINA_BENCHMARK=1, on Linux with Docker.";
        SkipUnless = nameof(Enabled);
        SkipType = typeof(BenchmarkFactAttribute);
    }

    public static bool Enabled => Environment.GetEnvironmentVariable("OFFICINA_BENCHMARK") == "1" && BookshopDatabase.Available;
}
