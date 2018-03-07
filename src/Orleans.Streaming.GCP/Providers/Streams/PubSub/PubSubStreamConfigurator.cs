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
            : base(name, builder)
        {
            this.siloBuilder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(PubSubAdapterFactory<>).Assembly))
                .AddPersistentStreams(name, PubSubAdapterFactory<TDataAdapter>.Create)
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<PubSubOptions>(name)
                        .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                        .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
                });
        }

        public SiloPubSubStreamConfigurator<TDataAdapter> ConfigureAzureQueue(Action<OptionsBuilder<PubSubOptions>> configureOptions)
        {
            this.Configure<PubSubOptions>(configureOptions);
            return this;
        }
        public SiloPubSubStreamConfigurator<TDataAdapter> ConfigureCacheSize(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
            return this;
        }

        public SiloPubSubStreamConfigurator<TDataAdapter> ConfigureQueueMapper(int numOfQueues = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.NumQueues = numOfQueues));
            return this;
        }
    }

    public class ClusterClientPubSubStreamConfigurator<TDataAdapter> : ClusterClientPersistentStreamConfigurator
        where TDataAdapter : IPubSubDataAdapter
    {
        public ClusterClientPubSubStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder)
        {
            this.clientBuilder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(PubSubAdapterFactory<>).Assembly))
                .AddPersistentStreams(name, PubSubAdapterFactory<TDataAdapter>.Create)
                .ConfigureServices(services => services.ConfigureNamedOptionForLogging<PubSubOptions>(name));
        }

        public ClusterClientPubSubStreamConfigurator<TDataAdapter> ConfigureAzureQueue(Action<OptionsBuilder<PubSubOptions>> configureOptions)
        {
            this.Configure<PubSubOptions>(configureOptions);
            return this;
        }
    }
}
