using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.Membership;

namespace Orleans;

/// <summary>
/// Configures MongoDB services for Orleans.
/// </summary>
public static class MongoDBClusteringClientBuilderExtensions
{
    /// <summary>
    /// Configure client to use MongoGatewayListProvider
    /// </summary>
    public static IClientBuilder UseMongoDBClustering(this IClientBuilder builder,
        Action<MongoDBGatewayListProviderOptions>? configurator = null)
    {
        return builder.ConfigureServices(services => services.AddMongoDBGatewayListProvider(configurator));
    }
    /// <summary>
    /// Configure client to use MongoGatewayListProvider
    /// </summary>
    public static IClientBuilder UseMongoDBClustering(this IClientBuilder builder,
        IConfiguration configuration)
    {
        return builder.ConfigureServices(services => services.AddMongoDBGatewayListProvider(configuration));
    }
    /// <summary>
    /// Configure silo to use MongoGatewayListProvider.
    /// </summary>
    public static IServiceCollection AddMongoDBGatewayListProvider(this IServiceCollection services,
        Action<MongoDBGatewayListProviderOptions>? configurator = null)
    {
        services.AddOptions<MongoDBGatewayListProviderOptions>();
        if (configurator is not null)
        {
            services.Configure(configurator);
        }
        services.AddSingleton<IGatewayListProvider, MongoGatewayListProvider>();
        services.AddSingleton<IConfigurationValidator, MongoDBOptionsValidator<MongoDBGatewayListProviderOptions>>();

        return services;
    }
    /// <summary>
    /// Configure silo to use MongoGatewayListProvider.
    /// </summary>
    public static IServiceCollection AddMongoDBGatewayListProvider(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<MongoDBGatewayListProviderOptions>(configuration);
        services.AddSingleton<IGatewayListProvider, MongoGatewayListProvider>();
        services.AddSingleton<IConfigurationValidator, MongoDBOptionsValidator<MongoDBGatewayListProviderOptions>>();

        return services;
    }
}
