using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.Kinesis;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configures an Amazon Kinesis Data Streams-backed persistent stream provider on an Orleans client.
    /// </summary>
    public class ClusterClientKinesisStreamConfigurator : ClusterClientPersistentStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClientKinesisStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="builder">The client builder.</param>
        public ClusterClientKinesisStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, KinesisAdapterFactory.Create)
        {
            this.ConfigureDelegate(services =>
            {
                services.ConfigureNamedOptionForLogging<KinesisStreamOptions>(name)
                    .ConfigureNamedOptionForLogging<RecoverableStreamReplayOptions>(name)
                    .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name)
                    .AddTransient<IConfigurationValidator>(sp => new KinesisStreamOptionsValidator(sp.GetOptionsByName<KinesisStreamOptions>(name), name));
            });
        }

        /// <summary>
        /// Configures the Kinesis options for the stream provider.
        /// </summary>
        /// <param name="configureOptions">
        /// The delegate used to configure the named <see cref="KinesisStreamOptions"/>.
        /// </param>
        /// <returns>The stream provider configurator.</returns>
        public ClusterClientKinesisStreamConfigurator ConfigureKinesis(Action<OptionsBuilder<KinesisStreamOptions>> configureOptions)
        {
            this.Configure(configureOptions);
            return this;
        }

        /// <summary>
        /// Configures the Kinesis options for the stream provider.
        /// </summary>
        /// <param name="configureOptions">The delegate used to configure the Kinesis options.</param>
        /// <returns>The stream provider configurator.</returns>
        public ClusterClientKinesisStreamConfigurator ConfigureKinesis(Action<KinesisStreamOptions> configureOptions)
        {
            this.ConfigureKinesis(ob => ob.Configure(configureOptions));
            return this;
        }

        /// <summary>
        /// Configures retained-history readers and replay buffering for this provider.
        /// </summary>
        /// <param name="configureOptions">The replay configuration delegate.</param>
        /// <returns>This configurator.</returns>
        public ClusterClientKinesisStreamConfigurator ConfigureReplay(Action<OptionsBuilder<RecoverableStreamReplayOptions>> configureOptions)
        {
            this.Configure(configureOptions);
            return this;
        }
    }
}
