using System;
using Orleans.Configuration;
using Orleans.Streams;
using OrleansAWSUtils.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use SQS persistent streams.
        /// </summary>
        public static SiloSqsStreamConfigurator AddSqsStreams(this ISiloHostBuilder builder, string name)
        {
            return new SiloSqsStreamConfigurator(name, builder);
        }
    }
}