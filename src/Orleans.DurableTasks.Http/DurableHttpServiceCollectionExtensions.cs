using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.DurableTasks.Http;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the services which serve durable tasks as HTTP operations.</summary>
public static class DurableHttpServiceCollectionExtensions
{
    /// <summary>
    /// Adds the services which the durable operation endpoints use, which
    /// <see cref="Microsoft.AspNetCore.Builder.DurableHttpEndpointRouteBuilderExtensions"/> maps.
    /// </summary>
    /// <param name="services">The service collection, which also provides an Orleans <see cref="Orleans.IGrainFactory"/>.</param>
    /// <param name="configureOptions">Configures the operations, or <see langword="null"/> for the defaults.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddDurableHttp(this IServiceCollection services, Action<DurableHttpOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<DurableHttpOptions>();
        if (configureOptions is not null)
        {
            options.Configure(configureOptions);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DurableOperationService>();
        services.TryAddSingleton<DurableHttpOperations>();

        return services;
    }
}
