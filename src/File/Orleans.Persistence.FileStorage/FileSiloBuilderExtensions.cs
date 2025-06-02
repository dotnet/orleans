using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

namespace Orleans.Persistence.FileStorage;

public static class FileSiloBuilderExtensions
{
    public static ISiloBuilder AddFileGrainStorage(
        this ISiloBuilder builder,
        string providerName,
        Action<FileGrainStorageOptions> options) =>
        builder.ConfigureServices(
            services => services.AddFileGrainStorage(
                providerName, options));

    public static IServiceCollection AddFileGrainStorage(
        this IServiceCollection services,
        string providerName,
        Action<FileGrainStorageOptions> options)
    {
        services.AddOptions<FileGrainStorageOptions>(providerName)
            .Configure(options);

        services.AddTransient<
            IPostConfigureOptions<FileGrainStorageOptions>,
            DefaultStorageProviderSerializerOptionsConfigurator<FileGrainStorageOptions>>();

        return services.AddGrainStorage(providerName, FileGrainStorageFactory.Create);
    }
}
