using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Storage;
using Orleans.Providers;
using Orleans.Persistence.Cosmos;

namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring Azure Cosmos DB persistence.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TPartitionKeyProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault<TPartitionKeyProvider>(
        this ISiloBuilder builder,
        Action<CosmosGrainStorageOptions> configureOptions) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        return builder.AddCosmosGrainStorage<TPartitionKeyProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TPartitionKeyProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorage<TPartitionKeyProvider>(
        this ISiloBuilder builder,
        string name,
        Action<CosmosGrainStorageOptions> configureOptions) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        builder.Services.TryAddSingleton<IPartitionKeyProvider, TPartitionKeyProvider>();
        builder.Services.AddCosmosGrainStorage(name, configureOptions);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <param name="customPartitionKeyProviderType">The custom partition key provider type.</param>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<CosmosGrainStorageOptions> configureOptions,
        Type customPartitionKeyProviderType)
    {
        return builder.AddCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions, customPartitionKeyProviderType);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <param name="customPartitionKeyProviderType">The custom partition key provider type.</param>
    public static ISiloBuilder AddCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<CosmosGrainStorageOptions> configureOptions,
        Type customPartitionKeyProviderType)
    {
        if (customPartitionKeyProviderType != null)
        {
            builder.Services.TryAddSingleton(typeof(IPartitionKeyProvider), customPartitionKeyProviderType);
        }

        builder.Services.AddCosmosGrainStorage(name, configureOptions);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<CosmosGrainStorageOptions> configureOptions)
    {
        return builder.AddCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<CosmosGrainStorageOptions> configureOptions)
    {
        builder.Services.AddCosmosGrainStorage(name, configureOptions);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TPartitionKeyProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault<TPartitionKeyProvider>(
        this ISiloBuilder builder,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        return builder.AddCosmosGrainStorage<TPartitionKeyProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TPartitionKeyProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorage<TPartitionKeyProvider>(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        builder.Services.TryAddSingleton<IPartitionKeyProvider, TPartitionKeyProvider>();
        builder.Services.AddCosmosGrainStorage(name, configureOptions);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="customPartitionKeyProviderType">The custom partition key provider type.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Type customPartitionKeyProviderType,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
    {
        return builder.AddCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, customPartitionKeyProviderType, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Type customPartitionKeyProviderType,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
    {
        if (customPartitionKeyProviderType != null)
        {
            builder.Services.TryAddSingleton(typeof(IPartitionKeyProvider), customPartitionKeyProviderType);
        }

        builder.Services.AddCosmosGrainStorage(name, configureOptions);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
    {
        return builder.AddCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
    {
        builder.Services.AddCosmosGrainStorage(name, configureOptions);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static IServiceCollection AddCosmosGrainStorageAsDefault(
        this IServiceCollection services,
        Action<CosmosGrainStorageOptions> configureOptions)
    {
        return services.AddCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static IServiceCollection AddCosmosGrainStorage(
        this IServiceCollection services,
        string name,
        Action<CosmosGrainStorageOptions> configureOptions)
    {
        return services.AddCosmosGrainStorage(name, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static IServiceCollection AddCosmosGrainStorageAsDefault(
        this IServiceCollection services,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
    {
        return services.AddCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static IServiceCollection AddCosmosGrainStorage(
        this IServiceCollection services,
        string name,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
    {
        configureOptions?.Invoke(services.AddOptions<CosmosGrainStorageOptions>(name));
        services.AddTransient<IConfigurationValidator>(
            sp => new CosmosOptionsValidator<CosmosGrainStorageOptions>(
                sp.GetService<IOptionsMonitor<CosmosGrainStorageOptions>>()!.Get(name),
                name));
        services.ConfigureNamedOptionForLogging<CosmosGrainStorageOptions>(name);
        services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        services.TryAddSingleton<IPartitionKeyProvider, DefaultPartitionKeyProvider>();
        return services.AddSingletonNamedService(name, CosmosStorageFactory.Create)
            .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
    }
}