using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.LeaseProviders;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static ISiloBuilder AddAzureQueueStreams(this ISiloBuilder builder, string name,
            Action<SiloAzureQueueStreamConfigurator> configure)
        {
            var configurator = new SiloAzureQueueStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams with default settings
        /// </summary>
        public static ISiloBuilder AddAzureQueueStreams(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            builder.AddAzureQueueStreams(name, b =>
                 b.ConfigureAzureQueue(configureOptions));
            return builder;
        }

        /// <summary>
        /// Configure silo to use azure blob lease provider
        /// </summary>
        public static ISiloBuilder UseAzureBlobLeaseProvider(this ISiloBuilder builder, Action<OptionsBuilder<AzureBlobLeaseProviderOptions>> configureOptions)
        {
            builder.ConfigureServices(services => ConfigureAzureBlobLeaseProviderServices(services, configureOptions));
            return builder;
        }

        private static void ConfigureAzureBlobLeaseProviderServices(IServiceCollection services, Action<OptionsBuilder<AzureBlobLeaseProviderOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<AzureBlobLeaseProviderOptions>());
            services.AddTransient<IConfigurationValidator, AzureBlobLeaseProviderOptionsValidator>();
            services.ConfigureFormatter<AzureBlobLeaseProviderOptions>();
            services.AddTransient<AzureBlobLeaseProvider>();
            services.AddFromExisting<ILeaseProvider, AzureBlobLeaseProvider>();
        }

        /// <summary>
        /// Configure silo to use azure blob lease provider
        /// </summary>
        public static void UseAzureBlobLeaseProvider(this ISiloPersistentStreamConfigurator configurator, Action<OptionsBuilder<AzureBlobLeaseProviderOptions>> configureOptions)
        {
            configurator.ConfigureDelegate(services =>
            {
                services.AddTransient(sp => AzureBlobLeaseProviderOptionsValidator.Create(sp, configurator.Name));
            });
            configurator.ConfigureComponent(AzureBlobLeaseProvider.Create, configureOptions);
        }
    }
}
