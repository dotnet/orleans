using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Hosting
{
    public static class AzureTableSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure table storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableGrainStorageAsDefault(this ISiloBuilder builder, Action<AzureTableStorageOptions> configureOptions)
        {
            return builder.AddAzureTableGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableGrainStorage(this ISiloBuilder builder, string name, Action<AzureTableStorageOptions> configureOptions)
        {
            return builder.AddAzureTableGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use azure table storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AzureTableStorageOptions>> configureOptions = null)
        {
            return builder.AddAzureTableGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureTableStorageOptions>> configureOptions = null)
        {
            return builder.AddGrainStorage(name, configure =>
            {
                configure.UseOrleansSerializer();
                configure.UseAzureTable(configureOptions);
            });
        }

        /// <summary>
        /// Use Azure Table as grain storage
        /// </summary>
        public static void UseAzureTable(this IGrainStorageProviderConfigurator configurator, Action<AzureTableStorageOptions> options)
        {
            configurator.UseAzureTable(builder => builder.Configure(options));
        }

        /// <summary>
        /// Use Azure Table as grain storage
        /// </summary>
        public static void UseAzureTable(this IGrainStorageProviderConfigurator configurator, Action<OptionsBuilder<AzureTableStorageOptions>> configureOptions)
        {
            configurator.ConfigureStorage(AzureTableGrainStorageFactory.Create, configureOptions);
            configurator.ConfigureDelegate.Invoke(services =>
            {
                services.AddSingletonNamedService(
                  configurator.Name,
                  (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
                services.AddTransient<IConfigurationValidator>(sp => new AzureTableGrainStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureTableStorageOptions>>().Get(configurator.Name), configurator.Name));
            });
        }
    }
}
