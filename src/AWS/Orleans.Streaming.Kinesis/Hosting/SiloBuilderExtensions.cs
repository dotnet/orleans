using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use Kinesis Data Stream streaming with default settings.
        /// </summary>
        /// <remarks>
        /// This overload uses the Orleans grain-based checkpointer. Checkpoints are persisted using the
        /// <c>PubSubStore</c> grain storage provider and do not require DynamoDB or the Kinesis Client Library.
        /// </remarks>
        public static ISiloBuilder AddKinesisStreams(this ISiloBuilder builder, string name, Action<KinesisStreamOptions> configureOptions)
        {
            builder.AddKinesisStreams(name, b =>
            {
                b.ConfigureKinesis(ob => ob.Configure(configureOptions));
                b.UseGrainCheckpointer(ob => ob.Configure(options =>
                    options.CheckpointComparer = StreamCheckpointComparers.Numeric));
            });

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
