using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Storage;
using Orleans.Persistence.Cassandra;

namespace Orleans.Hosting;

/// <summary>Registration extensions for Cassandra grain storage.</summary>
public static class CassandraGrainStorageServiceCollectionExtensions
{
    /// <summary>Adds Cassandra grain storage as the default provider.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The provider configuration callback.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddCassandraGrainStorageAsDefault(this IServiceCollection services, Action<CassandraGrainStorageOptions> configureOptions) =>
        services.AddCassandraGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, options => options.Configure(configureOptions));

    /// <summary>Adds Cassandra grain storage as the default provider.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The provider configuration callback.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddCassandraGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<CassandraGrainStorageOptions>>? configureOptions = null) =>
        services.AddCassandraGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);

    /// <summary>Adds a named Cassandra grain storage provider.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configureOptions">The provider configuration callback.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddCassandraGrainStorage(this IServiceCollection services, string name, Action<OptionsBuilder<CassandraGrainStorageOptions>>? configureOptions = null)
    {
        configureOptions?.Invoke(services.AddOptions<CassandraGrainStorageOptions>(name));
        services.ConfigureNamedOptionForLogging<CassandraGrainStorageOptions>(name);
        services.AddTransient<IPostConfigureOptions<CassandraGrainStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<CassandraGrainStorageOptions>>();
        services.AddTransient<IConfigurationValidator>(sp => new CassandraGrainStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<CassandraGrainStorageOptions>>().Get(name), name));
        return services.AddGrainStorage(name, CassandraGrainStorageFactory.Create);
    }

    /// <summary>Adds a named Cassandra grain storage provider.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configureOptions">The provider configuration callback.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddCassandraGrainStorage(this IServiceCollection services, string name, Action<CassandraGrainStorageOptions> configureOptions) =>
        services.AddCassandraGrainStorage(name, options => options.Configure(configureOptions));
}

/// <summary>Silo builder extensions for Cassandra grain storage.</summary>
public static class CassandraGrainStorageSiloBuilderExtensions
{
    /// <summary>Adds Cassandra grain storage as the default provider.</summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The provider configuration callback.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddCassandraGrainStorageAsDefault(this ISiloBuilder builder, Action<CassandraGrainStorageOptions> configureOptions) =>
        builder.ConfigureServices(services => services.AddCassandraGrainStorageAsDefault(configureOptions));

    /// <summary>Adds Cassandra grain storage as the default provider.</summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The provider configuration callback.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddCassandraGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<CassandraGrainStorageOptions>>? configureOptions = null) =>
        builder.ConfigureServices(services => services.AddCassandraGrainStorageAsDefault(configureOptions));

    /// <summary>Adds a named Cassandra grain storage provider.</summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configureOptions">The provider configuration callback.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddCassandraGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<CassandraGrainStorageOptions>>? configureOptions = null) =>
        builder.ConfigureServices(services => services.AddCassandraGrainStorage(name, configureOptions));

    /// <summary>Adds a named Cassandra grain storage provider.</summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configureOptions">The provider configuration callback.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddCassandraGrainStorage(this ISiloBuilder builder, string name, Action<CassandraGrainStorageOptions> configureOptions) =>
        builder.ConfigureServices(services => services.AddCassandraGrainStorage(name, configureOptions));
}
