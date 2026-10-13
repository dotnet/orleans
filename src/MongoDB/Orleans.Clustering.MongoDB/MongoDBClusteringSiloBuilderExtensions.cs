using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.Membership;
using Orleans.Runtime;

namespace Orleans.Hosting;

/// <summary>
/// Configures MongoDB services for Orleans.
/// </summary>
public static class MongoDBClusteringSiloBuilderExtensions
{
    /// <summary>
    /// Configure ISiloBuilder to use MongoBasedMembership
    /// </summary>
    public static ISiloBuilder UseMongoDBClustering(this ISiloBuilder builder,
        Action<MongoDBMembershipTableOptions>? configurator = null)
    {
        return builder.ConfigureServices(services => services.AddMongoDBMembershipTable(configurator));
    }
    /// <summary>
    /// Configure ISiloBuilder to use MongoMembershipTable
    /// </summary>
    public static ISiloBuilder UseMongoDBMembershipTable(this ISiloBuilder builder,
        IConfiguration configuration)
    {
        return builder.ConfigureServices(services => services.AddMongoDBMembershipTable(configuration));
    }
    /// <summary>
    /// Configure silo to use MongoMembershipTable.
    /// </summary>
    public static IServiceCollection AddMongoDBMembershipTable(this IServiceCollection services,
        Action<MongoDBMembershipTableOptions>? configurator = null)
    {
        services.AddOptions<MongoDBMembershipTableOptions>();
        if (configurator is not null)
        {
            services.Configure(configurator);
        }
        services.AddSingleton<IMembershipTable, MongoMembershipTable>();
        services.AddSingleton<IConfigurationValidator, MongoDBOptionsValidator<MongoDBMembershipTableOptions>>();

        return services;
    }
    /// <summary>
    /// Configure silo to use MongoMembershipTable.
    /// </summary>
    public static IServiceCollection AddMongoDBMembershipTable(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<MongoDBMembershipTableOptions>(configuration);
        services.AddSingleton<IMembershipTable, MongoMembershipTable>();
        services.AddSingleton<IConfigurationValidator, MongoDBOptionsValidator<MongoDBMembershipTableOptions>>();

        return services;
    }
}
