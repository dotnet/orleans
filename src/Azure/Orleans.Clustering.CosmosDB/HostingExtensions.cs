using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Clustering.CosmosDB;

namespace Orleans.Hosting;

public static class HostingExtensions
{
    public static ISiloBuilder UseAzureCosmosDBClustering(this ISiloBuilder builder,
       Action<AzureCosmosDBClusteringOptions> configureOptions)
    {
        return builder.ConfigureServices(services => services.UseAzureCosmosDBClustering(configureOptions));
    }

    public static ISiloBuilder UseAzureCosmosDBClustering(this ISiloBuilder builder,
        Action<OptionsBuilder<AzureCosmosDBClusteringOptions>> configureOptions)
    {
        return builder.ConfigureServices(services => services.UseAzureCosmosDBClustering(configureOptions));
    }

    public static ISiloBuilder UseAzureCosmosDBClustering(this ISiloBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            services.AddOptions<AzureCosmosDBClusteringOptions>();
            services.AddSingleton<IMembershipTable, AzureCosmosDBMembershipTable>();
        });
    }

    public static IClientBuilder UseAzureCosmosDBGatewayListProvider(this IClientBuilder builder, Action<AzureCosmosDBClusteringOptions> configureOptions)
    {
        return builder.ConfigureServices(services => services.UseAzureCosmosDBGatewayListProvider(configureOptions));
    }

    public static IClientBuilder UseAzureCosmosDBGatewayListProvider(this IClientBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            services.AddOptions<AzureCosmosDBClusteringOptions>();
            services.AddSingleton<IGatewayListProvider, AzureCosmosDBGatewayListProvider>();
        });
    }

    public static IClientBuilder UseAzureCosmosDBGatewayListProvider(this IClientBuilder builder, Action<OptionsBuilder<AzureCosmosDBClusteringOptions>> configureOptions)
    {
        return builder.ConfigureServices(services => services.UseAzureCosmosDBGatewayListProvider(configureOptions));
    }

    public static IServiceCollection UseAzureCosmosDBClustering(this IServiceCollection services,
        Action<AzureCosmosDBClusteringOptions> configureOptions)
    {
        return services.UseAzureCosmosDBClustering(ob => ob.Configure(configureOptions));
    }

    public static IServiceCollection UseAzureCosmosDBClustering(this IServiceCollection services,
        Action<OptionsBuilder<AzureCosmosDBClusteringOptions>> configureOptions)
    {
        configureOptions?.Invoke(services.AddOptions<AzureCosmosDBClusteringOptions>());
        return services.AddSingleton<IMembershipTable, AzureCosmosDBMembershipTable>();
    }

    public static IServiceCollection UseAzureCosmosDBGatewayListProvider(this IServiceCollection services,
        Action<AzureCosmosDBClusteringOptions> configureOptions)
    {
        return services.UseAzureCosmosDBGatewayListProvider(ob => ob.Configure(configureOptions));
    }

    public static IServiceCollection UseAzureCosmosDBGatewayListProvider(this IServiceCollection services,
        Action<OptionsBuilder<AzureCosmosDBClusteringOptions>> configureOptions)
    {
        configureOptions?.Invoke(services.AddOptions<AzureCosmosDBClusteringOptions>());
        services.AddTransient<IConfigurationValidator>(
            sp => new AzureCosmosDBOptionsValidator<AzureCosmosDBClusteringOptions>(
                sp.GetRequiredService<IOptionsMonitor<AzureCosmosDBClusteringOptions>>().CurrentValue,
                nameof(AzureCosmosDBClusteringOptions)));
        return services.AddSingleton<IGatewayListProvider, AzureCosmosDBGatewayListProvider>();
    }
}