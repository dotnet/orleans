using System;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use SQS persistent streams.
        /// </summary>
        public static ISiloBuilder AddSqsStreams(this ISiloBuilder builder, string name, Action<SqsOptions> configureOptions)
        {
            builder.AddSqsStreams(name, b =>
                b.ConfigureSqs(ob => ob.Configure(configureOptions)));
            return builder;
        }

        /// <summary>
        /// Configure silo to use SQS persistent streams.
        /// </summary>
        public static ISiloBuilder AddSqsStreams(this ISiloBuilder builder, string name, Action<SiloSqsStreamConfigurator> configure)
        {
            var configurator = new SiloSqsStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
            configure?.Invoke(configurator);
            return builder;
        }
    }
}