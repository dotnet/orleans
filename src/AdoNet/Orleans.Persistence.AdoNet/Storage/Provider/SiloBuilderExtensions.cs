﻿using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use AdoNet grain storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAdoNetGrainStorageAsDefault(this ISiloHostBuilder builder, Action<AdoNetGrainStorageOptions> configureOptions)
        {
            return builder.AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use  AdoNet grain storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAdoNetGrainStorage(this ISiloHostBuilder builder, string name, Action<AdoNetGrainStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAdoNetGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use  AdoNet grain storage as the default grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAdoNetGrainStorageAsDefault(this ISiloHostBuilder builder, Action<OptionsBuilder<AdoNetGrainStorageOptions>> configureOptions = null)
        {
            return builder.AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AdoNet grain storage for grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAdoNetGrainStorage(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<AdoNetGrainStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddAdoNetGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use  AdoNet grain storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddAdoNetGrainStorage(this IServiceCollection services, Action<AdoNetGrainStorageOptions> configureOptions)
        {
            return services.AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use AdoNet grain storage for grain storage.
        /// </summary>
        public static IServiceCollection AddAdoNetGrainStorage(this IServiceCollection services, string name, Action<AdoNetGrainStorageOptions> configureOptions)
        {
            return services.AddAdoNetGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use AdoNet grain storage as the default grain storage.
        /// </summary>
        public static IServiceCollection AddAdoNetGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<AdoNetGrainStorageOptions>> configureOptions = null)
        {
            return services.AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AdoNet grain storage for grain storage.
        /// </summary>
        public static IServiceCollection AddAdoNetGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<AdoNetGrainStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<AdoNetGrainStorageOptions>(name));
            services.ConfigureNamedOptionForLogging<AdoNetGrainStorageOptions>(name);
            services.TryAddSingleton<IGrainStorage>(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddTransient<IConfigurationValidator>(sp => new AdoNetGrainStorageOptionsValidator(sp.GetService<IOptionsSnapshot<AdoNetGrainStorageOptions>>().Get(name), name));
            return services.AddSingletonNamedService<IGrainStorage>(name, AdoNetGrainStorageFactory.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
        }
    }
}
