using Microsoft.Extensions.DependencyInjection;

namespace Sleepyshark.Officina.Tests;

/// <summary>The core registers its built-in memory stores only when asked.</summary>
public sealed class OfficinaServicesTests
{
    [Fact]
    public void The_file_memory_store_is_the_memory_store()
    {
        using var services = new ServiceCollection().AddFileMemoryStore("memory").BuildServiceProvider();

        Assert.IsType<FileMemoryStore>(services.GetRequiredService<IMemoryStore>());
    }

    [Fact]
    public void The_in_memory_store_is_the_memory_store()
    {
        using var services = new ServiceCollection().AddInMemoryMemoryStore().BuildServiceProvider();

        Assert.IsType<InMemoryMemoryStore>(services.GetRequiredService<IMemoryStore>());
    }

    [Fact]
    public void A_file_memory_store_needs_a_folder()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddFileMemoryStore(" "));
    }
}
