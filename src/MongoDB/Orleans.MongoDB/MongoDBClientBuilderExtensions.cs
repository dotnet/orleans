using System;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Orleans.Hosting;

namespace Orleans;

/// <summary>
/// Configures MongoDB services for Orleans.
/// </summary>
public static class MongoDBClientBuilderExtensions
{
    /// <summary>
    /// Registers a shared MongoDB client for gateway discovery.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    public static IClientBuilder UseMongoDBClient(this IClientBuilder builder, string connectionString)
    {
        return builder.ConfigureServices(services => services.AddMongoDBClient(connectionString));
    }
    /// <summary>
    /// Registers a shared MongoDB client using application-configured settings.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="settingsFactory">The settings factory.</param>
    /// <returns>The client builder.</returns>
    public static IClientBuilder UseMongoDBClient(this IClientBuilder builder, Func<IServiceProvider, MongoClientSettings> settingsFactory)
    {
        return builder.ConfigureServices(services => services.AddMongoDBClient(settingsFactory));
    }
}
