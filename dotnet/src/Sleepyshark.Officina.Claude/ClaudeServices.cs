using Microsoft.Extensions.DependencyInjection;

namespace Sleepyshark.Officina.Claude;

/// <summary>Registers Claude models.</summary>
public static class ClaudeServices
{
    /// <summary>
    /// A Claude model as the <see cref="IModel"/> under <paramref name="key"/>, made by <paramref name="create"/> when
    /// first needed; the container disposes it.
    /// </summary>
    public static IServiceCollection AddClaudeModel(this IServiceCollection services, object? key, Func<IServiceProvider, ClaudeModel> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return services.AddKeyedSingleton<IModel>(key, (provider, _) => create(provider));
    }
}
