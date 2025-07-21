using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use azure queue persistent streams.
        /// </summary>
        public static IClientBuilder AddAzureQueueStreams(this IClientBuilder builder,
            string name,
            Action<ClusterClientAzureQueueStreamConfigurator> configure)
        {
            //the constructor wires up DI with AzureQueueStream, so has to be called regardless configure is null or not
            var configurator = new ClusterClientAzureQueueStreamConfigurator(name, builder);
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure cluster client to use azure queue persistent streams.
        /// </summary>
        public static IClientBuilder AddAzureQueueStreams(this IClientBuilder builder,
            string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            builder.AddAzureQueueStreams(name, b => b.ConfigureAzureQueue(configureOptions));
            return builder;
        }

        /// <summary>
        /// Configure cluster client to use Azure Queue persistent streams with JSON serialization.
        /// This feature is experimental and subject to change in future updates.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configure">Configuration delegate for the JSON-enabled Azure Queue stream provider.</param>
        /// <returns>The client builder for method chaining.</returns>
        [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
        public static IClientBuilder AddAzureQueueJsonStreams(this IClientBuilder builder,
            string name,
            Action<ClusterClientAzureQueueJsonStreamConfigurator> configure)
        {
            var configurator = new ClusterClientAzureQueueJsonStreamConfigurator(name, builder);
            configure?.Invoke(configurator);
            return builder;
        }

        /// <summary>
        /// Configure cluster client to use Azure Queue persistent streams with JSON serialization and default settings.
        /// This feature is experimental and subject to change in future updates.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureOptions">Configuration delegate for Azure Queue options.</param>
        /// <returns>The client builder for method chaining.</returns>
        [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
        public static IClientBuilder AddAzureQueueJsonStreams(this IClientBuilder builder,
            string name, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            builder.AddAzureQueueJsonStreams(name, b=> b.ConfigureAzureQueue(configureOptions));
            return builder;
        }
    }
}
