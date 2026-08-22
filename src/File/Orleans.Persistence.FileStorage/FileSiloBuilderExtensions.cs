using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

namespace Orleans.Persistence.FileStorage;

/// <summary>
/// Extensions for configuring file grain storage.
/// </summary>
public static class FileSiloBuilderExtensions
{
    /// <summary>
    /// Configures file grain storage as the default grain storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="options">The configuration delegate.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddFileGrainStorage(
        this ISiloBuilder builder,
        Action<FileGrainStorageOptions> options) =>
        builder.AddFileGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, options);

    /// <summary>
    /// Configures a named file grain storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="providerName">The storage provider name.</param>
    /// <param name="options">The configuration delegate.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddFileGrainStorage(
        this ISiloBuilder builder,
        string providerName,
        Action<FileGrainStorageOptions> options) =>
        builder.ConfigureServices(
            services => services.AddFileGrainStorage(
                providerName, options));

    /// <summary>
    /// Configures file grain storage as the default grain storage provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The configuration delegate.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddFileGrainStorage(
        this IServiceCollection services,
        Action<FileGrainStorageOptions> options) =>
        services.AddFileGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, options);

    /// <summary>
    /// Configures a named file grain storage provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="providerName">The storage provider name.</param>
    /// <param name="options">The configuration delegate.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddFileGrainStorage(
        this IServiceCollection services,
        string providerName,
        Action<FileGrainStorageOptions> options)
    {
        services.AddOptions<FileGrainStorageOptions>(providerName)
            .Configure(options);

        services.AddTransient<IConfigurationValidator>(
            serviceProvider => new FileGrainStorageOptionsValidator(
                serviceProvider.GetRequiredService<IOptionsMonitor<FileGrainStorageOptions>>().Get(providerName),
                providerName));
        services.AddTransient<
            IPostConfigureOptions<FileGrainStorageOptions>,
            DefaultStorageProviderSerializerOptionsConfigurator<FileGrainStorageOptions>>();

        return services.AddGrainStorage(providerName, FileGrainStorageFactory.Create);
    }
}
