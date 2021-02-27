using System;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {   /// <summary>
        /// Configure cluster client to use SQS persistent streams with default settings
        /// </summary>
        public static IClientBuilder AddSqsStreams(this IClientBuilder builder, string name, Action<SqsOptions> configureOptions)
        {
            builder.AddSqsStreams(name, b=>
                b.ConfigureSqs(ob=>ob.Configure(configureOptions)));
            return builder;
        }

        /// <summary>
        /// Configure cluster client to use SQS persistent streams.
        /// </summary>
        public static IClientBuilder AddSqsStreams(this IClientBuilder builder, string name, Action<ClusterClientSqsStreamConfigurator> configure)
        {
            var configurator = new ClusterClientSqsStreamConfigurator(name, builder);
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
