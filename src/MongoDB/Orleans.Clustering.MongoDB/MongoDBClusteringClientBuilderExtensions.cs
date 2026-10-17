using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Clustering.MongoDB;
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
        services.AddSingleton<IGatewayListProvider>(sp => ActivatorUtilities.CreateInstance<MongoGatewayListProvider>(
            sp, sp.GetMongoClient(sp.GetRequiredService<IOptions<MongoDBGatewayListProviderOptions>>().Value)));
        services.AddSingleton<IConfigurationValidator, MongoDBOptionsValidator<MongoDBGatewayListProviderOptions>>();

        return services;
    }
    /// <summary>
    /// Configure silo to use MongoGatewayListProvider.
    /// </summary>
    public static IServiceCollection AddMongoDBGatewayListProvider(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MongoDBGatewayListProviderOptions>()
            .Configure<IServiceProvider>((options, sp) => options.ConfigureMongoDBOptions(sp, configuration));
        services.AddSingleton<IGatewayListProvider>(sp => ActivatorUtilities.CreateInstance<MongoGatewayListProvider>(
            sp, sp.GetMongoClient(sp.GetRequiredService<IOptions<MongoDBGatewayListProviderOptions>>().Value)));
        services.AddSingleton<IConfigurationValidator, MongoDBOptionsValidator<MongoDBGatewayListProviderOptions>>();

        return services;
    }
}
