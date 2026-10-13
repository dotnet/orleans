using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Orleans.Providers.MongoDB.Utils;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers shared MongoDB clients and client factories for Orleans providers.
/// </summary>
public static class MongoDBServiceCollectionExtensions
{
    /// <summary>
    /// Registers a shared MongoDB client using the supplied connection string.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddMongoDBClient(this IServiceCollection services, string connectionString)
    {
        return services.AddMongoDBClient(provider => MongoClientSettings.FromConnectionString(connectionString));
    }

    /// <summary>
    /// Registers a shared MongoDB client using application-configured settings.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="settingsFactory">The settings factory.</param>
    /// <returns>The service collection.</returns>
    /// <exception cref="ArgumentNullException">The settings factory is null.</exception>
    public static IServiceCollection AddMongoDBClient(this IServiceCollection services, Func<IServiceProvider, MongoClientSettings> settingsFactory)
    {
        ArgumentNullException.ThrowIfNull(settingsFactory);

        services.TryAddSingleton<IMongoClient>(provider =>
        {
            var settings = settingsFactory(provider);
            return new MongoClient(settings);
        });

        services.TryAddSingleton<IMongoClientFactory, DefaultMongoClientFactory>();

        return services;
    }
}
