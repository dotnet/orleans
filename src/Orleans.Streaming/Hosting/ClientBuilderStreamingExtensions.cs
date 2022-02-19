using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Configuration.Internal;
using System.Linq;
using Orleans.Providers.Streams.SimpleMessageStream;

namespace Orleans.Hosting
{
    public static class ClientBuilderStreamingExtensions
    {
        /// <summary>
        /// Adds support for streaming to this client.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddStreaming(this IClientBuilder builder) => builder.ConfigureServices(AddClientStreaming);

        /// <summary>
        /// Add support for streaming to this client.
        /// </summary>
        /// <param name="services">The services.</param>
        public static void AddClientStreaming(this IServiceCollection services)
        {
            if (services.Any(service => service.ServiceType.Equals(typeof(ClientStreamingProviderRuntime))))
            {
                return;
            }

            services.AddSingleton<ClientStreamingProviderRuntime>();
            services.AddFromExisting<IStreamProviderRuntime, ClientStreamingProviderRuntime>();
            services.AddSingleton<IStreamSubscriptionManagerAdmin, StreamSubscriptionManagerAdmin>();
            services.AddSingleton<ImplicitStreamSubscriberTable>();
            services.AddSingleton<IStreamNamespacePredicateProvider, DefaultStreamNamespacePredicateProvider>();
            services.AddSingleton<IStreamNamespacePredicateProvider, ConstructorStreamNamespacePredicateProvider>();
            services.AddSingletonKeyedService<string, IStreamIdMapper, DefaultStreamIdMapper>(DefaultStreamIdMapper.Name);
            services.AddFromExisting<ILifecycleParticipant<IClusterClientLifecycle>, ClientStreamingProviderRuntime>();
        }

        /// <summary>
        /// Adds a new in-memory stream provider to the client.
        /// </summary>
        /// <typeparam name="TSerializer">The type of the t serializer.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddMemoryStreams<TSerializer>(
            this IClientBuilder builder,
            string name,
            Action<IClusterClientMemoryStreamConfigurator> configure = null)
            where TSerializer : class, IMemoryMessageBodySerializer
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var memoryStreamConfigurator = new ClusterClientMemoryStreamConfigurator<TSerializer>(name, builder);
            configure?.Invoke(memoryStreamConfigurator);
            return builder;
        }

        /// <summary>
        /// Adds a new persistent streams provider to the client.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="adapterFactory">The adapter factory.</param>
        /// <param name="configureStream">The configuration delegate.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddPersistentStreams(
            this IClientBuilder builder,
            string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<IClusterClientPersistentStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new ClusterClientPersistentStreamConfigurator(name, builder, adapterFactory);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Adds a new simple message streams provider to the client.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="configureStream">The configuration delegate.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddSimpleMessageStreamProvider(
            this IClientBuilder builder,
            string name,
            Action<ISimpleMessageStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SimpleMessageStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate), builder);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Adds a new simple message streams provider to the client.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddSimpleMessageStreamProvider(
            this IClientBuilder builder,
            string name,
            Action<SimpleMessageStreamProviderOptions> configureOptions)
        {
            return AddSimpleMessageStreamProvider(builder, name, b => b
                .Configure<SimpleMessageStreamProviderOptions>(ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Adds a new simple message streams provider to the client.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddSimpleMessageStreamProvider(
            this IClientBuilder builder,
            string name,
            Action<OptionsBuilder<SimpleMessageStreamProviderOptions>> configureOptions = null)
        {
            return AddSimpleMessageStreamProvider(builder, name, b => b
                .Configure(configureOptions));
        }
    }
}