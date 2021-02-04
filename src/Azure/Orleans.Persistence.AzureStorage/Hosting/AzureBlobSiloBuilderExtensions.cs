using System;
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
    public static class AzureBlobSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureBlobGrainStorageAsDefault(this ISiloBuilder builder, Action<AzureBlobStorageOptions> configureOptions)
        {
            return builder.AddAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureBlobGrainStorage(this ISiloBuilder builder, string name, Action<AzureBlobStorageOptions> configureOptions)
        {
            return builder.AddGrainStorage(name, configure =>
            {
                configure.UseOrleansSerializer();
                configure.UseAzureBlob(configureOptions);
            });
        }

        /// <summary>
        /// Configure silo to use azure blob storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureBlobGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            return builder.AddAzureBlobGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure blob storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureBlobGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions = null)
        {
            return builder.AddGrainStorage(name, configure =>
            {
                configure.UseOrleansSerializer();
                configure.UseAzureBlob(configureOptions);
            });
        }

        /// <summary>
        /// Use Azure Blob as grain storage
        /// </summary>
        public static void UseAzureBlob(this IGrainStorageProviderConfigurator configurator, Action<AzureBlobStorageOptions> options)
        {
            configurator.UseAzureBlob(builder => builder.Configure(options));
        }

        /// <summary>
        /// Use Azure Blob as grain storage
        /// </summary>
        public static void UseAzureBlob(this IGrainStorageProviderConfigurator configurator, Action<OptionsBuilder<AzureBlobStorageOptions>> configureOptions)
        {
            configurator.ConfigureStorage(AzureBlobGrainStorageFactory.Create, configureOptions);
            configurator.ConfigureDelegate.Invoke(services =>
            {
                services.AddSingletonNamedService(
                  configurator.Name,
                  (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
                services
                .AddTransient<IConfigurationValidator>(
                    sp => new AzureBlobStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>().Get(configurator.Name), configurator.Name));
            });
        }
    }
}
