using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Sleepyshark.Officina;

namespace BookshopAssistant.Tests;

/// <summary>The application's own registrations, with nothing replaced, make every service it uses.</summary>
public sealed class ServicesTests
{
    [Fact]
    public async Task Every_service_the_application_uses_resolves_from_its_own_registrations()
    {
        var settings = new BookshopSettings { Database = "Host=localhost;Username=bookshop;Password=secret-1;Database=bookshop" };
        await using var services = new ServiceCollection().AddBookshop(settings, demo: false).AddBookshopTelemetry(settings).Build();

        Assert.IsType<FileMemoryStore>(services.GetRequiredService<IMemoryStore>());
        Assert.Same(services.GetRequiredService<BookshopConsole>(), services.GetRequiredService<IApprover>());
        var chat = services.GetRequiredKeyedService<Agent>(BookshopServices.Chat);
        Assert.Equal(["secret-1"], chat.Secrets);
        Assert.Same(services.GetRequiredService<BookshopConsole>(), chat.Approver);
        Assert.NotSame(chat.Model, services.GetRequiredKeyedService<Agent>(BookshopServices.Summarizer).Model);
        Assert.NotNull(services.GetRequiredService<TracerProvider>());
        Assert.NotNull(services.GetRequiredService<MeterProvider>());
    }
}
