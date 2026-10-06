using System.Runtime.CompilerServices;

namespace BookshopAssistant.Tests;

/// <summary>A test against the database in Docker: skipped where <see cref="BookshopDatabase.Available"/> is false.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Needs Linux and Docker.";
        SkipUnless = nameof(BookshopDatabase.Available);
        SkipType = typeof(BookshopDatabase);
    }
}
