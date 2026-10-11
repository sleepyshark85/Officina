using System.Diagnostics;
using System.Text.Json;
using Sleepyshark.Officina;

namespace BookshopAssistant.Tests;

/// <summary>
/// The book and customer searches against a large catalogue, through the tools' own queries: each median of five runs is
/// written to the test output and must stay under a bound well above it, so an index the planner stops using is caught.
/// </summary>
[Trait("Category", "Benchmark")]
public class SearchBenchmarkTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    private const int Runs = 5;

    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(50);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [BenchmarkFact]
    public async Task Every_search_of_a_million_books_and_200k_customers_answers_within_50_ms()
    {
        await LoadAsync();
        var tools = new BookshopTools(database.DataSource);
        var rareTitle = await database.ScalarAsync<string>("select substr(md5('b' || 123457), 1, 6)");
        var rareAuthor = await database.ScalarAsync<string>("select substr(md5('a' || 4321), 1, 6)");
        var rareCustomer = await database.ScalarAsync<string>("select substr(md5('c' || 98765), 1, 7)");
        (string Name, Func<Task<ToolOutput>> Search)[] searches =
        [
            ("browse", () => tools.SearchBooksAsync(cancellationToken: Ct)),
            ("rare title", () => tools.SearchBooksAsync(title: rareTitle, cancellationToken: Ct)),
            ("common title", () => tools.SearchBooksAsync(title: "silver", cancellationToken: Ct)),
            ("rare author", () => tools.SearchBooksAsync(author: rareAuthor, cancellationToken: Ct)),
            ("genre, price, in stock", () => tools.SearchBooksAsync(genre: "fantasy", maxPrice: 10m, inStock: true, cancellationToken: Ct)),
            ("rare customer", () => tools.FindCustomerAsync(rareCustomer, Ct)),
            ("common customer", () => tools.FindCustomerAsync("emma", Ct)),
        ];

        var medians = new List<(string Name, TimeSpan Median)>();
        foreach (var (name, search) in searches)
        {
            var output = await search();
            Assert.False(output.IsError, output.Content);
            Assert.NotEqual(0, JsonDocument.Parse(output.Content).RootElement.GetArrayLength());
            medians.Add((name, await MedianAsync(search)));
        }

        foreach (var (name, median) in medians)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{name,-24} {median.TotalMilliseconds,8:0.0} ms");
        }

        Assert.All(medians, each => Assert.True(each.Median < Bound, $"{each.Name}: {each.Median.TotalMilliseconds:0.0} ms"));
    }

    private async Task LoadAsync()
    {
        await using var command = database.DataSource.CreateCommand(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Benchmark", "large-catalogue.sql"), Ct));
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(Ct);

        // As autovacuum would after a bulk load: it folds the rows the GIN indexes hold aside into the indexes proper.
        await using var vacuum = database.DataSource.CreateCommand("vacuum analyze");
        vacuum.CommandTimeout = 0;
        await vacuum.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<TimeSpan> MedianAsync(Func<Task<ToolOutput>> search)
    {
        var times = new List<TimeSpan>();
        for (var run = 0; run < Runs; run++)
        {
            var started = Stopwatch.GetTimestamp();
            await search();
            times.Add(Stopwatch.GetElapsedTime(started));
        }

        return times.Order().ElementAt(Runs / 2);
    }
}
