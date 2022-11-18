using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Persistence.DynamoDB.Provider.Compression;
using Orleans.Persistence.DynamoDB.Provider.Compression.Interfaces;
using System.Collections.Generic;
using Orleans.Persistence.DynamoDB.Provider.Serialization;
using Orleans.Persistence.DynamoDB.Provider.Serialization.Interfaces;

namespace Orleans.Hosting
{
    public static class DynamoDBSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddDynamoDBGrainStorageAsDefault(this ISiloHostBuilder builder,
            Action<DynamoDBStorageOptions> configureOptions)
        {
            return builder.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddDynamoDBGrainStorage(this ISiloHostBuilder builder, string name,
            Action<DynamoDBStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddDynamoDBGrainStorageAsDefault(this ISiloHostBuilder builder,
            Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return builder.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddDynamoDBGrainStorage(this ISiloHostBuilder builder, string name,
            Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorageAsDefault(this ISiloBuilder builder,
            Action<DynamoDBStorageOptions> configureOptions)
        {
            return builder.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorage(this ISiloBuilder builder, string name,
            Action<DynamoDBStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorageAsDefault(this ISiloBuilder builder,
            Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return builder.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorage(this ISiloBuilder builder, string name,
            Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddDynamoDBGrainStorageAsDefault(this IServiceCollection services,
            Action<DynamoDBStorageOptions> configureOptions)
        {
            return services.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
                ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static IServiceCollection AddDynamoDBGrainStorage(this IServiceCollection services, string name,
            Action<DynamoDBStorageOptions> configureOptions)
        {
            return services.AddDynamoDBGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddDynamoDBGrainStorageAsDefault(this IServiceCollection services,
            Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return services.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static IServiceCollection AddDynamoDBGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<DynamoDBStorageOptions>(name));
            services.AddTransient<IConfigurationValidator>(sp =>
                new DynamoDBGrainStorageOptionsValidator(
                    sp.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>().Get(name), name));
            services.ConfigureNamedOptionForLogging<DynamoDBStorageOptions>(name);
            
            if (string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal))
            {
                services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
                services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStateCompressionManager>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
                services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStateSerializationManager>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
                services.TryAddSingleton(sp => sp.GetServiceByName<IEnumerable<IProvideGrainStateRecordCompression>>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
                services.TryAddSingleton(sp => sp.GetServiceByName<IEnumerable<IProvideGrainStateRecordSerialization>>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            }

            return services
                .AddSingletonNamedService(name, DynamoDBGrainStorageFactory.Create)
                .AddSingletonNamedService(name,
                    (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n))
                .AddSingletonNamedService<IGrainStateCompressionManager>(
                    name,
                    (provider, s) => ActivatorUtilities.CreateInstance<GrainStateCompressionManager>(
                        provider,
                        provider.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>().Get(s)))
                .AddSingletonNamedService<IGrainStateSerializationManager>(
                    name,
                    (provider, s) => ActivatorUtilities.CreateInstance<GrainStateSerializationManager>(
                        provider,
                        provider.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>().Get(s)))
                .AddSingletonNamedService<IEnumerable<IProvideGrainStateRecordCompression>>(name, (provider, s) =>
                {
                    var options = provider.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>().Get(s);
                    return new IProvideGrainStateRecordCompression[]
                    {
                        ActivatorUtilities.CreateInstance<GrainStateRecordGzipCompressionService>(
                            provider,
                            options),
                        ActivatorUtilities.CreateInstance<GrainStateRecordDeflateCompressionService>(
                            provider,
                            options)
                    };
                })
                .AddSingletonNamedService<IEnumerable<IProvideGrainStateRecordSerialization>>(name, (provider, s) =>
                {
                    var options = provider.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>().Get(s);
                    return new IProvideGrainStateRecordSerialization[]
                    {
                        ActivatorUtilities.CreateInstance<GrainStateRecordBinarySerializationService>(
                            provider,
                            options),
                        ActivatorUtilities.CreateInstance<GrainStateRecordJsonSerializationService>(
                            provider,
                            options)
                    };
                });
        }
    }
}
