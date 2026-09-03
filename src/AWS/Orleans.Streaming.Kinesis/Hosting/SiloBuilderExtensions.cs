using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Provides extension methods for configuring Amazon Kinesis Data Streams-backed persistent streams on Orleans silos.
    /// </summary>
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configures the silo to use an Amazon Kinesis Data Streams-backed persistent stream provider.
        /// </summary>
        /// <remarks>
        /// This overload uses the Orleans grain-based checkpointer. Checkpoints are persisted using the
        /// <c>PubSubStore</c> grain storage provider and do not require DynamoDB or the Kinesis Client Library.
        /// </remarks>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configureOptions">The delegate used to configure the Kinesis options.</param>
        /// <returns>The silo builder.</returns>
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
        /// Configures the silo to use an Amazon Kinesis Data Streams-backed persistent stream provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configure">The delegate used to configure the stream provider.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddKinesisStreams(this ISiloBuilder builder, string name, Action<SiloKinesisStreamConfigurator> configure)
        {
            var configurator = new SiloKinesisStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
