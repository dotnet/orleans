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
        public static ISiloHostBuilder AddSqsStreams(this ISiloHostBuilder builder, string name, Action<SqsOptions> configureOptions)
        {
            builder.AddSqsStreams(name, b=>
                b.ConfigureSqs(ob => ob.Configure(configureOptions)));
            return builder;
        }

        /// <summary>
        /// Configure silo to use SQS persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddSqsStreams(this ISiloHostBuilder builder, string name, Action<SiloSqsStreamConfigurator> configure)
        {
            var configurator = new SiloSqsStreamConfigurator(name, builder);
            configure?.Invoke(configurator);
            return builder;
        }
    }
}