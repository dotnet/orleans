﻿using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class DynamoDBSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddDynamoDBGrainStorageAsDefault(this ISiloHostBuilder builder, Action<DynamoDBStorageOptions> configureOptions)
        {
            return builder.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddDynamoDBGrainStorage(this ISiloHostBuilder builder, string name, Action<DynamoDBStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddDynamoDBGrainStorageAsDefault(this ISiloHostBuilder builder, Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return builder.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddDynamoDBGrainStorage(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorageAsDefault(this ISiloBuilder builder, Action<DynamoDBStorageOptions> configureOptions)
        {
            return builder.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorage(this ISiloBuilder builder, string name, Action<DynamoDBStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return builder.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddDynamoDBGrainStorageAsDefault(this IServiceCollection services, Action<DynamoDBStorageOptions> configureOptions)
        {
            return services.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static IServiceCollection AddDynamoDBGrainStorage(this IServiceCollection services, string name, Action<DynamoDBStorageOptions> configureOptions)
        {
            return services.AddDynamoDBGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddDynamoDBGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
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
            services.AddTransient<IConfigurationValidator>(sp => new DynamoDBGrainStorageOptionsValidator(sp.GetService<IOptionsSnapshot<DynamoDBStorageOptions>>().Get(name), name));
            services.ConfigureNamedOptionForLogging<DynamoDBStorageOptions>(name);
            services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            return services.AddSingletonNamedService(name, DynamoDBGrainStorageFactory.Create)
                           .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
        }
    }
}
