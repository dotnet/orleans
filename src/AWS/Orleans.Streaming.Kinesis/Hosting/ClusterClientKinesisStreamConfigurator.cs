using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.Kinesis;

namespace Orleans.Hosting
{
    public class ClusterClientKinesisStreamConfigurator : ClusterClientPersistentStreamConfigurator
    {
        public ClusterClientKinesisStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, KinesisAdapterFactory.Create)
        {
            this.ConfigureDelegate(services =>
            {
                services.ConfigureNamedOptionForLogging<KinesisStreamOptions>(name)
                    .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
            });
        }

        public ClusterClientKinesisStreamConfigurator ConfigureKinesis(Action<OptionsBuilder<KinesisStreamOptions>> configureOptions)
        {
            this.Configure(configureOptions);
            return this;
        }

        public ClusterClientKinesisStreamConfigurator ConfigureKinesis(Action<KinesisStreamOptions> configureOptions)
        {
            this.ConfigureKinesis(ob => ob.Configure(configureOptions));
            return this;
        }
    }
}
