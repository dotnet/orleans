// <file_silo_builder_extensions>
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

namespace GrainStorage;

public static class FileSiloBuilderExtensions
{
    public static ISiloBuilder AddFileGrainStorage(
        this ISiloBuilder builder,
        Action<FileGrainStorageOptions> options) =>
        builder.AddFileGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, options);

    public static ISiloBuilder AddFileGrainStorage(
        this ISiloBuilder builder,
        string providerName,
        Action<FileGrainStorageOptions> options) =>
        builder.ConfigureServices(
            services => services.AddFileGrainStorage(providerName, options));

    public static IServiceCollection AddFileGrainStorage(
        this IServiceCollection services,
        Action<FileGrainStorageOptions> options) =>
        services.AddFileGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, options);

    public static IServiceCollection AddFileGrainStorage(
        this IServiceCollection services,
        string providerName,
        Action<FileGrainStorageOptions> options)
    {
        services.AddOptions<FileGrainStorageOptions>(providerName)
            .Configure(options);

        // <storage_registration>
        services.AddTransient<IConfigurationValidator>(
            serviceProvider => new FileGrainStorageOptionsValidator(
                serviceProvider.GetRequiredService<IOptionsMonitor<FileGrainStorageOptions>>().Get(providerName),
                providerName));
        services.AddTransient<
            IPostConfigureOptions<FileGrainStorageOptions>,
            DefaultStorageProviderSerializerOptionsConfigurator<FileGrainStorageOptions>>();

        return services.AddGrainStorage(providerName, FileGrainStorageFactory.Create);
        // </storage_registration>
    }
}
// </file_silo_builder_extensions>
