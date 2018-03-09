using System;
using Orleans.Configuration;
using Orleans.Streams;
using OrleansAWSUtils.Streams;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {

        /// <summary>
        /// Configure cluster client to use SQS persistent streams. This will return a configurator which allows further configuration
        /// </summary>
        public static ClusterClientSqsStreamConfigurator ConfigureSqsStreams(this IClientBuilder builder, string name)
        {
            return new ClusterClientSqsStreamConfigurator(name, builder);
        }

        /// <summary>
        /// Configure cluster client to use SQS persistent streams with default settings
        /// </summary>
        public static IClientBuilder AddSqsStreams(this IClientBuilder builder, string name, Action<SqsOptions> configureOptions)
        {
            builder.ConfigureSqsStreams(name)
                .ConfigureSqs(ob=>ob.Configure(configureOptions));
            return builder;
        }
    }
}
