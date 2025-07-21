using System;
using System.Diagnostics.CodeAnalysis;
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
            var configurator = new SiloAzureQueueStreamConfigurator(name, configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use azure queue persistent streams with default settings
        /// </summary>
        public static ISiloBuilder AddAzureQueueStreams(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            builder.AddAzureQueueStreams(name, b => b.ConfigureAzureQueue(configureOptions));
            return builder;
        }

        /// <summary>
        /// Configure silo to use Azure Queue persistent streams with JSON serialization.
        /// This feature is experimental and subject to change in future updates.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configure">Configuration delegate for the JSON-enabled Azure Queue stream provider.</param>
        /// <returns>The silo builder for method chaining.</returns>
        [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
        public static ISiloBuilder AddAzureQueueJsonStreams(this ISiloBuilder builder, string name,
            Action<SiloAzureQueueJsonStreamConfigurator> configure)
        {
            var configurator = new SiloAzureQueueJsonStreamConfigurator(name, configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use Azure Queue persistent streams with JSON serialization and default settings.
        /// This feature is experimental and subject to change in future updates.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureOptions">Configuration delegate for Azure Queue options.</param>
        /// <returns>The silo builder for method chaining.</returns>
        [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
        public static ISiloBuilder AddAzureQueueJsonStreams(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            builder.AddAzureQueueJsonStreams(name, b =>b.ConfigureAzureQueue(configureOptions));
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
