using System;
using Orleans.Configuration;
using Orleans.Streams;
using OrleansAWSUtils.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use SQS persistent streams. This returns a configurator which allows further configuration
        /// </summary>
        public static SiloSqsStreamConfigurator ConfigureSqsStreams(this ISiloHostBuilder builder, string name)
        {
            return new SiloSqsStreamConfigurator(name, builder);
        }

        /// <summary>
        /// Configure silo to use SQS persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddSqsStreams(this ISiloHostBuilder builder, string name, Action<SqsOptions> configureOptions)
        {
            builder.ConfigureSqsStreams(name)
                .ConfigureSqs(ob => ob.Configure(configureOptions));
            return builder;
        }
    }
}