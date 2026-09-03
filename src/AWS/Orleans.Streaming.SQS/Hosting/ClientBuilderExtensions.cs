using System;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Provides extension methods for configuring Amazon SQS-backed persistent streams on Orleans clients.
    /// </summary>
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configures the client to use an Amazon SQS-backed persistent stream provider.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configureOptions">The delegate used to configure the SQS options.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddSqsStreams(this IClientBuilder builder, string name, Action<SqsOptions> configureOptions)
        {
            builder.AddSqsStreams(name, b=>
                b.ConfigureSqs(ob=>ob.Configure(configureOptions)));
            return builder;
        }

        /// <summary>
        /// Configures the client to use an Amazon SQS-backed persistent stream provider.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configure">The delegate used to configure the stream provider.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddSqsStreams(this IClientBuilder builder, string name, Action<ClusterClientSqsStreamConfigurator> configure)
        {
            var configurator = new ClusterClientSqsStreamConfigurator(name, builder);
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
