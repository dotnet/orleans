using System;
using Orleans.Streaming.Kinesis;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use Kinesis Data Stream persistent streams with default settings
        /// </summary>
        public static IClientBuilder AddKinesisStreams(this IClientBuilder builder, string name, Action<KinesisStreamOptions> configureOptions)
        {
            builder.AddKinesisStreams(name, b =>
                b.ConfigureKinesis(ob => ob.Configure(configureOptions)));
            return builder;
        }

        /// <summary>
        /// Configure cluster client to use Kinesis Data Stream persistent streams.
        /// </summary>
        public static IClientBuilder AddKinesisStreams(this IClientBuilder builder, string name, Action<ClusterClientKinesisStreamConfigurator> configure)
        {
            var configurator = new ClusterClientKinesisStreamConfigurator(name, builder);
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
