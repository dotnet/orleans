using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Storage;
using Orleans.Providers;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime.Hosting;

namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring Azure Cosmos DB persistence.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom document id provider
    /// or a legacy partition key provider.
    /// </summary>
    /// <typeparam name="TProvider">The document id or partition key provider.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault<TProvider>(
        this ISiloBuilder builder,
        Action<CosmosGrainStorageOptions> configureOptions) where TProvider : class
    {
        return builder.AddCosmosGrainStorage<TProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom document id provider
    /// or a legacy partition key provider.
    /// </summary>
    /// <typeparam name="TProvider">The document id or partition key provider.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorage<TProvider>(
        this ISiloBuilder builder,
        string name,
        Action<CosmosGrainStorageOptions> configureOptions) where TProvider : class
    {
        AddIdentifierProvider(builder.Services, name, typeof(TProvider));
        builder.Services.AddCosmosGrainStorage(name, configureOptions);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom document id provider
    /// or a legacy partition key provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <param name="customPartitionKeyProviderType">The document id or partition key provider.</param>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<CosmosGrainStorageOptions> configureOptions,
        Type customPartitionKeyProviderType)
    {
        return builder.AddCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions, customPartitionKeyProviderType);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom document id provider
    /// or a legacy partition key provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <param name="customPartitionKeyProviderType">The document id or partition key provider.</param>
    public static ISiloBuilder AddCosmosGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<CosmosGrainStorageOptions> configureOptions,
        Type customPartitionKeyProviderType)
    {
        if (customPartitionKeyProviderType != null)
        {
            AddIdentifierProvider(builder.Services, name, customPartitionKeyProviderType, registerUnkeyedPartitionProvider: true);
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
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom document id provider
    /// or a legacy partition key provider.
    /// </summary>
    /// <typeparam name="TProvider">The document id or partition key provider.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault<TProvider>(
        this ISiloBuilder builder,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null) where TProvider : class
    {
        return builder.AddCosmosGrainStorage<TProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom document id provider
    /// or a legacy partition key provider.
    /// </summary>
    /// <typeparam name="TProvider">The document id or partition key provider.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorage<TProvider>(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null) where TProvider : class
    {
        AddIdentifierProvider(builder.Services, name, typeof(TProvider));
        builder.Services.AddCosmosGrainStorage(name, configureOptions);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage as the default grain storage using a custom document id provider
    /// or a legacy partition key provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="customPartitionKeyProviderType">The document id or partition key provider.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddCosmosGrainStorageAsDefault(
        this ISiloBuilder builder,
        Type customPartitionKeyProviderType,
        Action<OptionsBuilder<CosmosGrainStorageOptions>>? configureOptions = null)
    {
        return builder.AddCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, customPartitionKeyProviderType, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Azure Cosmos DB storage for grain storage using a custom document id provider
    /// or a legacy partition key provider.
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
            AddIdentifierProvider(builder.Services, name, customPartitionKeyProviderType);
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
#pragma warning disable CS0618 // Type or member is obsolete
        services.TryAddSingleton<IPartitionKeyProvider, DefaultPartitionKeyProvider>();
#pragma warning restore CS0618 // Type or member is obsolete
        services.TryAddSingleton<DefaultDocumentIdProvider>();
        services.TryAddSingleton<IDocumentIdProvider>(sp => sp.GetRequiredService<DefaultDocumentIdProvider>());
        return services.AddGrainStorage(name, CosmosStorageFactory.Create);
    }

    private static void AddIdentifierProvider(
        IServiceCollection services,
        string name,
        Type providerType,
        bool registerUnkeyedPartitionProvider = false)
    {
        if (typeof(IDocumentIdProvider).IsAssignableFrom(providerType))
        {
            services.AddKeyedSingleton(typeof(IDocumentIdProvider), name, providerType);
            return;
        }

#pragma warning disable CS0618 // Type or member is obsolete
        if (typeof(IPartitionKeyProvider).IsAssignableFrom(providerType))
        {
            services.AddKeyedSingleton(typeof(IPartitionKeyProvider), name, providerType);
            if (registerUnkeyedPartitionProvider)
            {
                services.TryAddSingleton(typeof(IPartitionKeyProvider), providerType);
            }

            return;
        }

        throw new ArgumentException(
            $"Provider type {providerType} must implement {nameof(IDocumentIdProvider)} or {nameof(IPartitionKeyProvider)}.",
            nameof(providerType));
#pragma warning restore CS0618 // Type or member is obsolete
    }
}