using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.GCP.Streams.PubSub;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Streams
{
    public class SiloPubSubStreamConfigurator<TDataAdapter> : SiloPersistentStreamConfigurator
         where TDataAdapter : IPubSubDataAdapter
    {
        public SiloPubSubStreamConfigurator(string name, ISiloHostBuilder builder)
            : base(name, builder, PubSubAdapterFactory<TDataAdapter>.Create)
        {
            this.siloBuilder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(PubSubAdapterFactory<>).Assembly))
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<PubSubOptions>(name)
                        .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                        .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
                });
        }

        public SiloPubSubStreamConfigurator<TDataAdapter> ConfigurePubSub(Action<OptionsBuilder<PubSubOptions>> configureOptions)
        {
            this.Configure<PubSubOptions>(configureOptions);
            return this;
        }
        public SiloPubSubStreamConfigurator<TDataAdapter> ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
            return this;
        }

        public SiloPubSubStreamConfigurator<TDataAdapter> ConfigurePartitioning(int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfPartitions));
            return this;
        }
    }

    public class ClusterClientPubSubStreamConfigurator<TDataAdapter> : ClusterClientPersistentStreamConfigurator
        where TDataAdapter : IPubSubDataAdapter
    {
        public ClusterClientPubSubStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, PubSubAdapterFactory<TDataAdapter>.Create)
        {
            this.clientBuilder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(PubSubAdapterFactory<>).Assembly))
                .ConfigureServices(services => services.ConfigureNamedOptionForLogging<PubSubOptions>(name));
        }

        public ClusterClientPubSubStreamConfigurator<TDataAdapter> ConfigurePubSub(Action<OptionsBuilder<PubSubOptions>> configureOptions)
        {
            this.Configure<PubSubOptions>(configureOptions);
            return this;
        }

        public ClusterClientPubSubStreamConfigurator<TDataAdapter> ConfigurePartitioning(int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfPartitions));
            return this;
        }
    }
}
