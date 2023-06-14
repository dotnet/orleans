using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Clustering.AzureCosmos;

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
    public static ISiloBuilder UseAzureCosmosClustering(
        this ISiloBuilder builder,
        Action<AzureCosmosClusteringOptions> configureOptions)
    {
        builder.Services.UseAzureCosmosClustering(configureOptions);
        return builder;
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static ISiloBuilder UseAzureCosmosClustering(
        this ISiloBuilder builder,
        Action<OptionsBuilder<AzureCosmosClusteringOptions>> configureOptions)
    {
        builder.Services.UseAzureCosmosClustering(configureOptions);
        return builder;
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static ISiloBuilder UseAzureCosmosClustering(this ISiloBuilder builder)
    {
        builder.Services.AddOptions<AzureCosmosClusteringOptions>();
        builder.Services.AddSingleton<IMembershipTable, AzureCosmosMembershipTable>();
        return builder;
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static IClientBuilder UseAzureCosmosGatewayListProvider(
        this IClientBuilder builder,
        Action<AzureCosmosClusteringOptions> configureOptions)
    {
        builder.Services.UseAzureCosmosGatewayListProvider(configureOptions);
        return builder;
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static IClientBuilder UseAzureCosmosGatewayListProvider(this IClientBuilder builder)
    {
        builder.Services.AddOptions<AzureCosmosClusteringOptions>();
        builder.Services.AddSingleton<IGatewayListProvider, AzureCosmosGatewayListProvider>();
        return builder;
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="builder"/>.</returns>
    public static IClientBuilder UseAzureCosmosGatewayListProvider(
        this IClientBuilder builder,
        Action<OptionsBuilder<AzureCosmosClusteringOptions>> configureOptions)
    {
        builder.Services.UseAzureCosmosGatewayListProvider(configureOptions);
        return builder;
    }


    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="services"/>.</returns>
    public static IServiceCollection UseAzureCosmosClustering(
        this IServiceCollection services,
        Action<AzureCosmosClusteringOptions> configureOptions)
    {
        return services.UseAzureCosmosClustering(ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="services"/>.</returns>
    public static IServiceCollection UseAzureCosmosClustering(
        this IServiceCollection services,
        Action<OptionsBuilder<AzureCosmosClusteringOptions>> configureOptions)
    {
        configureOptions?.Invoke(services.AddOptions<AzureCosmosClusteringOptions>());
        return services.AddSingleton<IMembershipTable, AzureCosmosMembershipTable>();
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="services"/>.</returns>
    public static IServiceCollection UseAzureCosmosGatewayListProvider(
        this IServiceCollection services,
        Action<AzureCosmosClusteringOptions> configureOptions)
    {
        return services.UseAzureCosmosGatewayListProvider(ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Adds clustering backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The provided <paramref name="services"/>.</returns>
    public static IServiceCollection UseAzureCosmosGatewayListProvider(
        this IServiceCollection services,
        Action<OptionsBuilder<AzureCosmosClusteringOptions>> configureOptions)
    {
        configureOptions?.Invoke(services.AddOptions<AzureCosmosClusteringOptions>());
        services.AddTransient<IConfigurationValidator>(
            sp => new AzureCosmosOptionsValidator<AzureCosmosClusteringOptions>(
                sp.GetRequiredService<IOptionsMonitor<AzureCosmosClusteringOptions>>().CurrentValue,
                nameof(AzureCosmosClusteringOptions)));
        return services.AddSingleton<IGatewayListProvider, AzureCosmosGatewayListProvider>();
    }
}