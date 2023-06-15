using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Clustering.Cosmos;

namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring Azure Cosmos DB clustering.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static ISiloBuilder UseCosmosClustering(
        this ISiloBuilder builder,
        Action<CosmosClusteringOptions> configureOptions)
    {
        builder.Services.UseCosmosClustering(configureOptions);
        return builder;
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static ISiloBuilder UseCosmosClustering(
        this ISiloBuilder builder,
        Action<OptionsBuilder<CosmosClusteringOptions>> configureOptions)
    {
        builder.Services.UseCosmosClustering(configureOptions);
        return builder;
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static ISiloBuilder UseCosmosClustering(this ISiloBuilder builder)
    {
        builder.Services.AddOptions<CosmosClusteringOptions>();
        builder.Services.AddSingleton<IMembershipTable, CosmosMembershipTable>();
        return builder;
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static IClientBuilder UseCosmosGatewayListProvider(
        this IClientBuilder builder,
        Action<CosmosClusteringOptions> configureOptions)
    {
        builder.Services.UseCosmosGatewayListProvider(configureOptions);
        return builder;
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static IClientBuilder UseCosmosGatewayListProvider(this IClientBuilder builder)
    {
        builder.Services.AddOptions<CosmosClusteringOptions>();
        builder.Services.AddSingleton<IGatewayListProvider, CosmosGatewayListProvider>();
        return builder;
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static IClientBuilder UseCosmosGatewayListProvider(
        this IClientBuilder builder,
        Action<OptionsBuilder<CosmosClusteringOptions>> configureOptions)
    {
        builder.Services.UseCosmosGatewayListProvider(configureOptions);
        return builder;
    }


    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="services"/>.</returns>
    public static IServiceCollection UseCosmosClustering(
        this IServiceCollection services,
        Action<CosmosClusteringOptions> configureOptions)
    {
        return services.UseCosmosClustering(ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="services"/>.</returns>
    public static IServiceCollection UseCosmosClustering(
        this IServiceCollection services,
        Action<OptionsBuilder<CosmosClusteringOptions>> configureOptions)
    {
        configureOptions?.Invoke(services.AddOptions<CosmosClusteringOptions>());
        return services.AddSingleton<IMembershipTable, CosmosMembershipTable>();
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="services"/>.</returns>
    public static IServiceCollection UseCosmosGatewayListProvider(
        this IServiceCollection services,
        Action<CosmosClusteringOptions> configureOptions)
    {
        return services.UseCosmosGatewayListProvider(ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="services"/>.</returns>
    public static IServiceCollection UseCosmosGatewayListProvider(
        this IServiceCollection services,
        Action<OptionsBuilder<CosmosClusteringOptions>> configureOptions)
    {
        configureOptions?.Invoke(services.AddOptions<CosmosClusteringOptions>());
        services.AddTransient<IConfigurationValidator>(
            sp => new CosmosOptionsValidator<CosmosClusteringOptions>(
                sp.GetRequiredService<IOptionsMonitor<CosmosClusteringOptions>>().CurrentValue,
                nameof(CosmosClusteringOptions)));
        return services.AddSingleton<IGatewayListProvider, CosmosGatewayListProvider>();
    }
}