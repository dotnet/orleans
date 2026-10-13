using System;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Orleans.Hosting;

/// <summary>
/// Configures MongoDB services for Orleans.
/// </summary>
public static class MongoDBSiloBuilderExtensions
{
    /// <summary>
    /// Registers a shared MongoDB client for the silo's providers.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    public static ISiloBuilder UseMongoDBClient(this ISiloBuilder builder, string connectionString)
    {
        return builder.ConfigureServices(services => services.AddMongoDBClient(connectionString));
    }
    /// <summary>
    /// Registers a shared MongoDB client using application-configured settings.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="settingsFactory">The settings factory.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseMongoDBClient(this ISiloBuilder builder, Func<IServiceProvider, MongoClientSettings> settingsFactory)
    {
        return builder.ConfigureServices(services => services.AddMongoDBClient(settingsFactory));
    }
}
