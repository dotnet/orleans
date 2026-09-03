using System;
using Orleans.Streaming.Kinesis;

namespace Orleans.Hosting
{
    /// <summary>
    /// Provides extension methods for configuring Amazon Kinesis Data Streams-backed persistent streams on Orleans clients.
    /// </summary>
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configures the client to use an Amazon Kinesis Data Streams-backed persistent stream provider.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configureOptions">The delegate used to configure the Kinesis options.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddKinesisStreams(this IClientBuilder builder, string name, Action<KinesisStreamOptions> configureOptions)
        {
            builder.AddKinesisStreams(name, b =>
                b.ConfigureKinesis(ob => ob.Configure(configureOptions)));
            return builder;
        }

        /// <summary>
        /// Configures the client to use an Amazon Kinesis Data Streams-backed persistent stream provider.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configure">The delegate used to configure the stream provider.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddKinesisStreams(this IClientBuilder builder, string name, Action<ClusterClientKinesisStreamConfigurator> configure)
        {
            var configurator = new ClusterClientKinesisStreamConfigurator(name, builder);
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
