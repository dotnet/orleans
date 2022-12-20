using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Storage;
using Orleans.Providers;
using Orleans.Persistence.AzureCosmos;

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
    public static ISiloBuilder AddAzureCosmosGrainStorageAsDefault<TPartitionKeyProvider>(
        this ISiloBuilder builder,
        Action<AzureCosmosGrainStorageOptions> configureOptions) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        return builder.AddAzureCosmosGrainStorage<TPartitionKeyProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TPartitionKeyProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorage<TPartitionKeyProvider>(
        this ISiloBuilder builder,
        string name,
        Action<AzureCosmosGrainStorageOptions> configureOptions) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        return builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<IPartitionKeyProvider, TPartitionKeyProvider>();
            services.AddAzureCosmosGrainStorage(name, configureOptions);
        });
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <param name="customPartitionKeyProviderType">The custom partition key provider type.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<AzureCosmosGrainStorageOptions> configureOptions,
        Type customPartitionKeyProviderType)
    {
        return builder.AddAzureCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions, customPartitionKeyProviderType);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <param name="customPartitionKeyProviderType">The custom partition key provider type.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<AzureCosmosGrainStorageOptions> configureOptions,
        Type customPartitionKeyProviderType)
    {
        return builder.ConfigureServices(services =>
        {
            if (customPartitionKeyProviderType != null)
            {
                services.TryAddSingleton(typeof(IPartitionKeyProvider), customPartitionKeyProviderType);
            }
            services.AddAzureCosmosGrainStorage(name, configureOptions);
        });
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<AzureCosmosGrainStorageOptions> configureOptions)
    {
        return builder.AddAzureCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<AzureCosmosGrainStorageOptions> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddAzureCosmosGrainStorage(name, configureOptions));
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TPartitionKeyProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorageAsDefault<TPartitionKeyProvider>(
        this ISiloBuilder builder,
        Action<OptionsBuilder<AzureCosmosGrainStorageOptions>>? configureOptions = null) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        return builder.AddAzureCosmosGrainStorage<TPartitionKeyProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TPartitionKeyProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorage<TPartitionKeyProvider>(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<AzureCosmosGrainStorageOptions>>? configureOptions = null) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        return builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<IPartitionKeyProvider, TPartitionKeyProvider>();
            services.AddAzureCosmosGrainStorage(name, configureOptions);
        });
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="customPartitionKeyProviderType">The custom partition key provider type.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Type customPartitionKeyProviderType,
        Action<OptionsBuilder<AzureCosmosGrainStorageOptions>>? configureOptions = null)
    {
        return builder.AddAzureCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, customPartitionKeyProviderType, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Type customPartitionKeyProviderType,
        Action<OptionsBuilder<AzureCosmosGrainStorageOptions>>? configureOptions = null)
    {
        return builder.ConfigureServices(services =>
        {
            if (customPartitionKeyProviderType != null)
            {
                services.TryAddSingleton(typeof(IPartitionKeyProvider), customPartitionKeyProviderType);
            }
            services.AddAzureCosmosGrainStorage(name, configureOptions);
        });
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<OptionsBuilder<AzureCosmosGrainStorageOptions>>? configureOptions = null)
    {
        return builder.AddAzureCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddAzureCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<AzureCosmosGrainStorageOptions>>? configureOptions = null)
    {
        return builder.ConfigureServices(services => services.AddAzureCosmosGrainStorage(name, configureOptions));
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static IServiceCollection AddAzureCosmosGrainStorageAsDefault(
        this IServiceCollection services,
        Action<AzureCosmosGrainStorageOptions> configureOptions)
    {
        return services.AddAzureCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static IServiceCollection AddAzureCosmosGrainStorage(
        this IServiceCollection services,
        string name,
        Action<AzureCosmosGrainStorageOptions> configureOptions)
    {
        return services.AddAzureCosmosGrainStorage(name, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static IServiceCollection AddAzureCosmosGrainStorageAsDefault(
        this IServiceCollection services,
        Action<OptionsBuilder<AzureCosmosGrainStorageOptions>>? configureOptions = null)
    {
        return services.AddAzureCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static IServiceCollection AddAzureCosmosGrainStorage(
        this IServiceCollection services,
        string name,
        Action<OptionsBuilder<AzureCosmosGrainStorageOptions>>? configureOptions = null)
    {
        configureOptions?.Invoke(services.AddOptions<AzureCosmosGrainStorageOptions>(name));
        services.AddTransient<IConfigurationValidator>(
            sp => new AzureCosmosOptionsValidator<AzureCosmosGrainStorageOptions>(
                sp.GetService<IOptionsMonitor<AzureCosmosGrainStorageOptions>>()!.Get(name),
                name));
        services.ConfigureNamedOptionForLogging<AzureCosmosGrainStorageOptions>(name);
        services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        services.TryAddSingleton<IPartitionKeyProvider, DefaultPartitionKeyProvider>();
        return services.AddSingletonNamedService(name, AzureCosmosStorageFactory.Create)
            .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
    }
}