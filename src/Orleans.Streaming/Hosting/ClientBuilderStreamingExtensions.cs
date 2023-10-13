using System;
using Orleans.Providers;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class ClientBuilderStreamingExtensions
    {
        /// <summary>
        /// Adds support for streaming to this client.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddStreaming(this IClientBuilder builder) => builder.ConfigureServices(services => services.AddClientStreaming());

        /// <summary>
        /// Adds a new in-memory stream provider to the client, using the default message serializer
        /// (<see cref="DefaultMemoryMessageBodySerializer"/>).
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddMemoryStreams(
            this IClientBuilder builder,
            string name,
            Action<IClusterClientMemoryStreamConfigurator> configure = null)
        {
            return AddMemoryStreams<DefaultMemoryMessageBodySerializer>(builder, name, configure);
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
    }
}