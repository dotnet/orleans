using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Storage;
using Orleans.Providers;
using Orleans.Persistence.CosmosDB;

namespace Orleans.Hosting;

public static class HostingExtensions
{
    /// <summary>
    /// Configure silo to use Azure CosmosDB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorageAsDefault<TPartitionKeyProvider>(this ISiloBuilder builder, Action<AzureCosmosDBStorageOptions> configureOptions) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        return builder.AddCosmosDBGrainStorage<TPartitionKeyProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorage<TPartitionKeyProvider>(this ISiloBuilder builder, string name, Action<AzureCosmosDBStorageOptions> configureOptions) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        return builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<IPartitionKeyProvider, TPartitionKeyProvider>();
            services.AddCosmosDBGrainStorage(name, configureOptions);
        });
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorageAsDefault(this ISiloBuilder builder, Action<AzureCosmosDBStorageOptions> configureOptions, Type customPartitionKeyProviderType)
    {
        return builder.AddCosmosDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions, customPartitionKeyProviderType);
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorage(this ISiloBuilder builder, string name, Action<AzureCosmosDBStorageOptions> configureOptions, Type customPartitionKeyProviderType)
    {
        return builder.ConfigureServices(services =>
        {
            if (customPartitionKeyProviderType != null)
            {
                services.TryAddSingleton(typeof(IPartitionKeyProvider), customPartitionKeyProviderType);
            }
            services.AddCosmosDBGrainStorage(name, configureOptions);
        });
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage as the default grain storage.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorageAsDefault(this ISiloBuilder builder, Action<AzureCosmosDBStorageOptions> configureOptions)
    {
        return builder.AddCosmosDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage for grain storage.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorage(this ISiloBuilder builder, string name, Action<AzureCosmosDBStorageOptions> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddCosmosDBGrainStorage(name, configureOptions));
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorageAsDefault<TPartitionKeyProvider>(this ISiloBuilder builder, Action<OptionsBuilder<AzureCosmosDBStorageOptions>>? configureOptions = null) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        return builder.AddCosmosDBGrainStorage<TPartitionKeyProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorage<TPartitionKeyProvider>(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureCosmosDBStorageOptions>>? configureOptions = null) where TPartitionKeyProvider : class, IPartitionKeyProvider
    {
        return builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<IPartitionKeyProvider, TPartitionKeyProvider>();
            services.AddCosmosDBGrainStorage(name, configureOptions);
        });
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorageAsDefault(this ISiloBuilder builder, Type customPartitionKeyProviderType, Action<OptionsBuilder<AzureCosmosDBStorageOptions>>? configureOptions = null)
    {
        return builder.AddCosmosDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, customPartitionKeyProviderType, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorage(this ISiloBuilder builder, string name, Type customPartitionKeyProviderType, Action<OptionsBuilder<AzureCosmosDBStorageOptions>>? configureOptions = null)
    {
        return builder.ConfigureServices(services =>
        {
            if (customPartitionKeyProviderType != null)
            {
                services.TryAddSingleton(typeof(IPartitionKeyProvider), customPartitionKeyProviderType);
            }
            services.AddCosmosDBGrainStorage(name, configureOptions);
        });
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage as the default grain storage.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AzureCosmosDBStorageOptions>>? configureOptions = null)
    {
        return builder.AddCosmosDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage for grain storage.
    /// </summary>
    public static ISiloBuilder AddCosmosDBGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureCosmosDBStorageOptions>>? configureOptions = null)
    {
        return builder.ConfigureServices(services => services.AddCosmosDBGrainStorage(name, configureOptions));
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage as the default grain storage.
    /// </summary>
    public static IServiceCollection AddCosmosDBGrainStorageAsDefault(this IServiceCollection services, Action<AzureCosmosDBStorageOptions> configureOptions)
    {
        return services.AddCosmosDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage for grain storage.
    /// </summary>
    public static IServiceCollection AddCosmosDBGrainStorage(this IServiceCollection services, string name, Action<AzureCosmosDBStorageOptions> configureOptions)
    {
        return services.AddCosmosDBGrainStorage(name, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage as the default grain storage.
    /// </summary>
    public static IServiceCollection AddCosmosDBGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<AzureCosmosDBStorageOptions>>? configureOptions = null)
    {
        return services.AddCosmosDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure CosmosDB storage for grain storage.
    /// </summary>
    public static IServiceCollection AddCosmosDBGrainStorage(this IServiceCollection services, string name,
        Action<OptionsBuilder<AzureCosmosDBStorageOptions>>? configureOptions = null)
    {
        configureOptions?.Invoke(services.AddOptions<AzureCosmosDBStorageOptions>(name));
        services.AddTransient<IConfigurationValidator>(
            sp => new AzureCosmosDBOptionsValidator<AzureCosmosDBStorageOptions>(
                sp.GetService<IOptionsMonitor<AzureCosmosDBStorageOptions>>()!.Get(name),
                name));
        services.ConfigureNamedOptionForLogging<AzureCosmosDBStorageOptions>(name);
        services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        services.TryAddSingleton<IPartitionKeyProvider, DefaultPartitionKeyProvider>();
        return services.AddSingletonNamedService(name, AzureCosmosDBStorageFactory.Create)
            .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
    }
}