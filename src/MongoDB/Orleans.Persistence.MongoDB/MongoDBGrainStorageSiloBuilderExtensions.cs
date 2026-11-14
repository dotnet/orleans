using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Persistence.MongoDB;
using Orleans.Providers.MongoDB.StorageProviders;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Runtime.Hosting;

namespace Orleans.Hosting;

/// <summary>
/// Configures MongoDB services for Orleans.
/// </summary>
public static class MongoDBGrainStorageSiloBuilderExtensions
{
    /// <summary>
    /// Configure silo to use MongoDB for grain storage.
    /// </summary>
    public static ISiloBuilder AddMongoDBGrainStorage(this ISiloBuilder builder, string name,
        Action<MongoDBGrainStorageOptions>? configureOptions)
    {
        return builder.ConfigureServices(services => services.AddMongoDBGrainStorage(name, configureOptions));
    }
    /// <summary>
    /// Configure silo to use MongoDB as the default grain storage.
    /// </summary>
    public static ISiloBuilder AddMongoDBGrainStorageAsDefault(this ISiloBuilder builder,
        Action<MongoDBGrainStorageOptions>? configureOptions)
    {
        return builder.AddMongoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }
    /// <summary>
    /// Configure silo to use MongoDB as the default grain storage.
    /// </summary>
    public static ISiloBuilder AddMongoDBGrainStorageAsDefault(this ISiloBuilder builder,
        Action<OptionsBuilder<MongoDBGrainStorageOptions>>? configureOptions = null)
    {
        return builder.AddMongoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }
    /// <summary>
    /// Configure silo to use MongoDB for grain storage.
    /// </summary>
    public static ISiloBuilder AddMongoDBGrainStorage(this ISiloBuilder builder, string name,
        Action<OptionsBuilder<MongoDBGrainStorageOptions>>? configureOptions = null)
    {
        return builder.ConfigureServices(services => services.AddMongoDBGrainStorage(name, configureOptions));
    }
    /// <summary>
    /// Configure silo to use MongoDB as the default grain storage.
    /// </summary>
    public static IServiceCollection AddMongoDBGrainStorageAsDefault(this IServiceCollection services,
        Action<MongoDBGrainStorageOptions>? configureOptions)
    {
        return services.AddMongoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }
    /// <summary>
    /// Configure silo to use MongoDB for grain storage.
    /// </summary>
    public static IServiceCollection AddMongoDBGrainStorage(this IServiceCollection services, string name,
        Action<MongoDBGrainStorageOptions>? configureOptions)
    {
        return services.AddMongoDBGrainStorage(name, ob =>
        {
            if (configureOptions is not null)
            {
                ob.Configure(configureOptions);
            }
        });
    }
    /// <summary>
    /// Configure silo to use MongoDB as the default grain storage.
    /// </summary>
    public static IServiceCollection AddMongoDBGrainStorageAsDefault(this IServiceCollection services,
        Action<OptionsBuilder<MongoDBGrainStorageOptions>>? configureOptions = null)
    {
        return services.AddMongoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }
    /// <summary>
    /// Configure silo to use MongoDB for grain storage.
    /// </summary>
    public static IServiceCollection AddMongoDBGrainStorage(this IServiceCollection services, string name,
        Action<OptionsBuilder<MongoDBGrainStorageOptions>>? configureOptions = null)
    {
        var optionsBuilder = services.AddOptions<MongoDBGrainStorageOptions>(name);
        configureOptions?.Invoke(optionsBuilder);
        services.TryAddSingleton<IGrainStateSerializer, JsonGrainStateSerializer>();
        services.AddTransient<IConfigurationValidator>(sp => new MongoDBGrainStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>().Get(name), name));
        services.ConfigureNamedOptionForLogging<MongoDBGrainStorageOptions>(name);
        services.AddTransient<IPostConfigureOptions<MongoDBGrainStorageOptions>, MongoDBGrainStorageConfigurator>();
        return services.AddGrainStorage(name, MongoGrainStorageFactory.Create);
    }
}
