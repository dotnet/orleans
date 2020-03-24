using System;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.S3.Options;
using Orleans.Persistence.S3.Provider;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.S3.Hosting
{
    public static class S3SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use AWS S3 storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddS3GrainStorageAsDefault(this ISiloHostBuilder builder, Action<S3StorageOptions> configureOptions) => builder.AddS3GrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);

        /// <summary>
        /// Configure silo to use AWS S3 storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddS3GrainStorage(this ISiloHostBuilder builder, string name, Action<S3StorageOptions> configureOptions) => builder.ConfigureServices(services => services.AddS3GrainStorage(name, configureOptions));

        /// <summary>
        /// Configure silo to use AWS S3 storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddS3GrainStorageAsDefault(this ISiloHostBuilder builder, Action<OptionsBuilder<S3StorageOptions>> configureOptions = null) => builder.AddS3GrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);

        /// <summary>
        /// Configure silo to use AWS S3 storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddS3GrainStorage(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<S3StorageOptions>> configureOptions = null) => builder.ConfigureServices(services => services.AddS3GrainStorage(name, configureOptions));

        /// <summary>
        /// Configure silo to use AWS S3 storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddS3GrainStorage(this ISiloBuilder builder, Action<S3StorageOptions> configureOptions) => builder.AddS3GrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);

        /// <summary>
        /// Configure silo to use AWS S3 storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddS3GrainStorage(this ISiloBuilder builder, string name, Action<S3StorageOptions> configureOptions) => builder.ConfigureServices(services => services.AddS3GrainStorage(name, configureOptions));

        /// <summary>
        /// Configure silo to use AWS S3 storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddS3GrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<S3StorageOptions>> configureOptions = null) => builder.AddS3GrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);

        /// <summary>
        /// Configure silo to use AWS S3 storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddS3GrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<S3StorageOptions>> configureOptions = null) => builder.ConfigureServices(services => services.AddS3GrainStorage(name, configureOptions));

        /// <summary>
        /// Configure silo to use AWS S3 storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddS3GrainStorageAsDefault(this IServiceCollection services, Action<S3StorageOptions> configureOptions) => services.AddS3GrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));

        /// <summary>
        /// Configure silo to use AWS S3 storage for grain storage.
        /// </summary>
        public static IServiceCollection AddS3GrainStorage(this IServiceCollection services, string name, Action<S3StorageOptions> configureOptions) => services.AddS3GrainStorage(name, ob => ob.Configure(configureOptions));

        /// <summary>
        /// Configure silo to use AWS S3 storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddS3GrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<S3StorageOptions>> configureOptions = null) => services.AddS3GrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);

        /// <summary>
        /// Configure silo to use AWS S3 storage for grain storage.
        /// </summary>
        public static IServiceCollection AddS3GrainStorage(this IServiceCollection services, string name, Action<OptionsBuilder<S3StorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<S3StorageOptions>(name));
            //services.AddTransient<IConfigurationValidator>(sp => new S3GrainStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<S3StorageOptions>>().Get(name), name));
            services.ConfigureNamedOptionForLogging<S3StorageOptions>(name);
            services.AddSingletonNamedService<IS3GrainStorageKeyFormatter, DefaultS3GrainStorageKeyFormatter>(name);
            services.AddSingletonNamedService<IAmazonS3, AmazonS3Client>(name);
            services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            return services
                .AddSingletonNamedService<IGrainStorage>(name, (s, n) => ActivatorUtilities.CreateInstance<S3GrainStorage>(s, name, s.GetOptionsByName<S3StorageOptions>(name)))
                .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
        }
    }
}
