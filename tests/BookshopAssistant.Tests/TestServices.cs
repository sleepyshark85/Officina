using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;

namespace BookshopAssistant.Tests;

/// <summary>
/// The application's own container, on the test's database, with only the boundaries replaced: the model, the clock,
/// the terminal and memory. A later registration wins, so a test adds its own replacements the same way.
/// </summary>
internal static class TestServices
{
    /// <summary>Monday 5 October 2026, 08:00 UTC; the fake clock's local time zone is UTC.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Where the console links runs to their traces.</summary>
    public static readonly Uri Dashboard = new("http://dashboard.test/");

    /// <param name="database">The database every service uses.</param>
    /// <param name="model">The chat agent's model; the caller owns it.</param>
    /// <param name="summaries">The summarizer's model; without one, there is no summarizer and sessions are not summarized.</param>
    /// <param name="demo">Whether the agent runs in demo mode.</param>
    public static ServiceCollection Create(
        BookshopDatabase database, IModel model, IModel? summaries = null, bool demo = false)
    {
        var services = new ServiceCollection();
        services.AddBookshop(new BookshopSettings { Database = database.ConnectionString, DashboardUrl = Dashboard }, demo);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Start));
        services.AddInMemoryMemoryStore();
        services.AddKeyedSingleton(BookshopServices.Chat, model);
        if (summaries is null)
        {
            services.RemoveAllKeyed<Agent>(BookshopServices.Summarizer);
        }
        else
        {
            services.AddKeyedSingleton(BookshopServices.Summarizer, summaries);
        }

        return services;
    }

    public static ServiceProvider Build(this IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}
