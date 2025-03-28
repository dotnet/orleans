// <copyright file="HostingExtensions.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Persistence.Cosmos;
using Orleans.Providers;
using Orleans.Storage;

namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring Azure Cosmos DB persistence.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TDocumentIdProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault<TDocumentIdProvider>(
        this ISiloBuilder builder,
        Action<CosmosGrainStorageOptions> configureOptions)
        where TDocumentIdProvider : class, IDocumentIdProvider
    {
        return builder.AddCosmosGrainStorage<TDocumentIdProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TDocumentIdProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
    public static ISiloBuilder AddCosmosGrainStorage<TDocumentIdProvider>(
        this ISiloBuilder builder,
        string name,
        Action<CosmosGrainStorageOptions> configureOptions)
        where TDocumentIdProvider : class, IDocumentIdProvider
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingletonNamedService<IDocumentIdProvider, TDocumentIdProvider>(name);
            services.AddCosmosGrainStorage(name, configureOptions);
        });
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <param name="customDocumentIdProviderType">The custom partition key provider type.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<CosmosGrainStorageOptions> configureOptions,
        Type customDocumentIdProviderType)
    {
        return builder.AddCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions, customDocumentIdProviderType);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <param name="customDocumentIdProviderType">The custom partition key provider type.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
    public static ISiloBuilder AddCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<CosmosGrainStorageOptions> configureOptions,
        Type customDocumentIdProviderType)
    {
        if (customDocumentIdProviderType != null)
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingletonNamedService<IDocumentIdProvider>(name, customDocumentIdProviderType);
            });
        }

        builder.ConfigureServices(services =>
        {
            services.AddCosmosGrainStorage(name, configureOptions);
        });

        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
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
    /// <returns>The silo builder, for chaining method calls.</returns>
    public static ISiloBuilder AddCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<CosmosGrainStorageOptions> configureOptions)
    {
        builder.ConfigureServices(services =>
        {
            services.AddCosmosGrainStorage(name, configureOptions);
        });
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TDocumentIdProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault<TDocumentIdProvider>(
        this ISiloBuilder builder,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
        where TDocumentIdProvider : class, IDocumentIdProvider
    {
        return builder.AddCosmosGrainStorage<TDocumentIdProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <typeparam name="TDocumentIdProvider">The custom partition key provider type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
    public static ISiloBuilder AddCosmosGrainStorage<TDocumentIdProvider>(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
        where TDocumentIdProvider : class, IDocumentIdProvider
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingletonNamedService<IDocumentIdProvider, TDocumentIdProvider>(name);
            services.AddCosmosGrainStorage(name, configureOptions);
        });
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="customDocumentIdProviderType">The custom partition key provider type.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Type customDocumentIdProviderType,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
    {
        return builder.AddCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, customDocumentIdProviderType, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom Partition Key Provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="customDocumentIdProviderType">The partition key provider type.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
    public static ISiloBuilder AddCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Type customDocumentIdProviderType,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
    {
        if (customDocumentIdProviderType != null)
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingletonNamedService<IDocumentIdProvider>(name, customDocumentIdProviderType);
            });
        }

        builder.ConfigureServices(services =>
        {
            services.AddCosmosGrainStorage(name, configureOptions);
        });

        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
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
    /// <returns>The silo builder, for chaining method calls.</returns>
    public static ISiloBuilder AddCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
    {
        builder.ConfigureServices(services =>
        {
            services.AddCosmosGrainStorage(name, configureOptions);
        });

        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder, for chaining method calls.</returns>
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
    /// <returns>The silo builder, for chaining method calls.</returns>
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
    /// <returns>The silo builder, for chaining method calls.</returns>
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
    /// <returns>The silo builder, for chaining method calls.</returns>
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
        services.TryAddSingleton<IDocumentIdProvider, DefaultDocumentIdProvider>();
        return services.AddSingletonNamedService(name, CosmosStorageFactory.Create)
            .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
    }
}