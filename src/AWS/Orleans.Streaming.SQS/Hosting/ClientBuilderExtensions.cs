using System;
using Orleans.Configuration;
using Orleans.Streams;
using OrleansAWSUtils.Streams;

namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {

        /// <summary>
        /// Configure cluster client to use SQS persistent streams.
        /// </summary>
        public static ClusterClientSqsStreamConfigurator AddSqsStreams(this IClientBuilder builder, string name)
        {
            return new ClusterClientSqsStreamConfigurator(name, builder);
        }
    }
}
