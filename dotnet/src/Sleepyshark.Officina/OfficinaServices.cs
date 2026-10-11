using Microsoft.Extensions.DependencyInjection;

namespace Sleepyshark.Officina;

/// <summary>Registers the core's built-in services; the core registers none by itself.</summary>
public static class OfficinaServices
{
    /// <summary>Memory as files under <paramref name="root"/>, as the <see cref="IMemoryStore"/>.</summary>
    public static IServiceCollection AddFileMemoryStore(this IServiceCollection services, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return services.AddSingleton<IMemoryStore>(new FileMemoryStore(root));
    }

    /// <summary>Memory that lasts as long as the process, as the <see cref="IMemoryStore"/>.</summary>
    public static IServiceCollection AddInMemoryMemoryStore(this IServiceCollection services) =>
        services.AddSingleton<IMemoryStore>(new InMemoryMemoryStore());
}
