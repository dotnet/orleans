using System;
using Orleans.Streaming.Kinesis;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use Kinesis Data Stream streaming with default settings.
        /// </summary>
        public static ISiloBuilder AddKinesisStreams(this ISiloBuilder builder, string name, Action<KinesisStreamOptions> configureOptions)
        {
            builder.AddKinesisStreams(name, b =>
                b.ConfigureKinesis(ob => ob.Configure(configureOptions)));
            return builder;
        }

        /// <summary>
        /// Configure silo to use Kinesis Data Stream streaming.
        /// </summary>
        public static ISiloBuilder AddKinesisStreams(this ISiloBuilder builder, string name, Action<SiloKinesisStreamConfigurator> configure)
        {
            var configurator = new SiloKinesisStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
